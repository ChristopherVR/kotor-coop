using System;

namespace KotorCoop.Plugin;

/// <summary>
/// One-shot deferral for <see cref="OdysseyMovies.PlayMovieNow"/>. docs/research/cinematics.md
/// §10.8 found that calling it directly from <see cref="CheatController"/>/
/// <see cref="MidModuleMovieController"/> — both of which run inside <see cref="PluginHost.OnFrame"/>,
/// the callback <see cref="FrameHook"/> fires from INSIDE the hooked
/// <c>GDI32!SwapBuffers</c> IAT slot, at the very END of the frame, AFTER both
/// <c>CClientExoApp::MainLoop</c> and <c>CServerExoApp::MainLoop</c> have already run — hangs the
/// engine's main thread indefinitely (two different real movies, 4+ minutes / 60+ seconds, never
/// returned; §10.8).
///
/// §10.1's own call-graph shows the two callers the retail game actually has never call from
/// there: a script's own <c>PlayMovie</c> runs from inside <c>CVirtualMachine</c> execution,
/// which is itself inside <c>MainLoop</c>'s script/AI update — BEFORE the render pass and
/// BEFORE <c>SwapBuffers</c> — and the main-menu gallery's replay
/// (<c>CSWGuiTitleMovies::OnMoviePicked</c>) runs from a GUI input callback, off the message
/// pump, also never nested inside <c>SwapBuffers</c>.
///
/// <see cref="MainLoopHook"/> reaches that same "outside render, outside SwapBuffers" territory
/// on purpose (see its own header comment): it rewrites WinMain's OWN call site for
/// <c>CClientExoApp::MainLoop</c>, so its callback fires immediately BEFORE MainLoop runs for the
/// frame — before the script VM, before render, before the swap. Arming a call here and running
/// it from that callback (<see cref="PluginHost.MenuFrame"/>) reproduces the retail call sites'
/// actual position in the frame rather than the SwapBuffers-nested one that hung. See
/// docs/research/cinematics.md §10.9 for the live result.
///
/// Not thread-safe by design: both current callers (<see cref="CheatController.MoviePlay"/>,
/// <see cref="MidModuleMovieController.PlayNow"/>) run on the SAME game thread
/// <see cref="MainLoopHook"/>'s callback also runs on, one frame apart at most, and
/// <see cref="MovieStaging.DecidePlayNow"/> already treats "a movie is already playing" as a
/// refusal reason for anything ELSE — so two arms landing in the same frame would already be a
/// contradiction the staging check should have caught first.
/// </summary>
internal static class PendingMoviePlay
{
    private static Action? _action;

    /// <summary>True while something is armed and waiting for the next
    /// <see cref="MainLoopHook"/> callback to run it.</summary>
    public static bool IsArmed => _action != null;

    /// <summary>Arms one deferred call, replacing whatever was armed before (there should never
    /// be anything to replace — see the class doc).</summary>
    public static void Arm(Action action) => _action = action;

    /// <summary>Runs the armed action, if any, and un-arms. Called from
    /// <see cref="PluginHost.MenuFrame"/>, before <c>MainLoop</c> — the whole point.</summary>
    public static void RunIfArmed()
    {
        Action? action = _action;
        if (action == null)
        {
            return;
        }

        _action = null;
        action();
    }

    /// <summary>Un-arms without running. A module change (<c>PluginHost.InvalidateAll</c>) drops
    /// whatever was armed the same way it drops every other cross-world pending write — the
    /// resref, the target block fields, all of it named the OLD world.</summary>
    public static void Cancel() => _action = null;
}
