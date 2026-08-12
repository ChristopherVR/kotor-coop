namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Copies a parsed <see cref="GffStruct"/> into a <see cref="GffWriterStruct"/>, field for
/// field and type for type, recursing through nested structs and lists.
///
/// This exists for world adoption: a joining player's character has to be lifted out of
/// their own save's <c>module.ifo</c> (<c>Mod_PlayerList</c>) and re-emitted as a
/// standalone <c>Temp\pifo.ifo</c> so the engine's own
/// <c>LoadCharacterFromIFO(-1)</c> path can log them into somebody else's world. Nothing
/// is interpreted on the way through — the engine reads 117 fields off that entry and
/// this code knows the meaning of none of them, which is the point: a copy that
/// understood the character would have to be updated whenever the character could change.
///
/// ⚠ The copy is driven by the reader's RECORDED field type, never by the CLR type of the
/// decoded value. Several GFF types decode to the same CLR type (ExoString and ResRef are
/// both <c>string</c>; Vector and Orientation are both <c>float[]</c>), and choosing by
/// value would silently rewrite one as the other — a file the engine then reads with the
/// wrong parser.
/// </summary>
public static class GffCopy
{
    /// <summary>
    /// Copies every field of <paramref name="source"/> into <paramref name="destination"/>,
    /// preserving order, type and nested struct ids. Existing fields on the destination
    /// with the same label are replaced, matching the setters' own semantics.
    /// </summary>
    /// <exception cref="InvalidDataException">A field carries a type or value the writer
    /// cannot emit — which means the source came from a file that <see cref="Gff"/> decoded
    /// into something no writer could have produced.</exception>
    public static void CopyInto(GffStruct source, GffWriterStruct destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        foreach (string label in source.FieldNames)
        {
            if (!source.TryGetFieldType(label, out GffFieldType type)
                || !source.Fields.TryGetValue(label, out object? value))
            {
                // FieldNames and Fields are filled together, so this is unreachable short
                // of the reader changing shape. Skipping beats throwing on a field we
                // cannot even name a type for.
                continue;
            }

            try
            {
                CopyField(label, type, value, destination);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidCastException or NullReferenceException)
            {
                throw new InvalidDataException(
                    $"GFF copy: field '{label}' ({type}) could not be re-emitted: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Builds a complete standalone GFF of type <paramref name="fileType"/> whose
    /// root is a copy of <paramref name="source"/>.</summary>
    public static byte[] ToFile(GffStruct source, string fileType)
    {
        GffWriter writer = new(fileType);
        CopyInto(source, writer.Root);
        return writer.ToBytes();
    }

    private static void CopyField(string label, GffFieldType type, object value, GffWriterStruct destination)
    {
        switch (type)
        {
            case GffFieldType.Byte:
                destination.SetByte(label, (byte)value);
                break;
            case GffFieldType.Char:
                destination.SetChar(label, (sbyte)value);
                break;
            case GffFieldType.Word:
                destination.SetWord(label, (ushort)value);
                break;
            case GffFieldType.Short:
                destination.SetShort(label, (short)value);
                break;
            case GffFieldType.Dword:
                destination.SetDword(label, (uint)value);
                break;
            case GffFieldType.Int:
                destination.SetInt(label, (int)value);
                break;
            case GffFieldType.Dword64:
                destination.SetDword64(label, (ulong)value);
                break;
            case GffFieldType.Int64:
                destination.SetInt64(label, (long)value);
                break;
            case GffFieldType.Float:
                destination.SetFloat(label, (float)value);
                break;
            case GffFieldType.Double:
                destination.SetDouble(label, (double)value);
                break;
            case GffFieldType.ExoString:
                destination.SetString(label, (string)value);
                break;
            case GffFieldType.ResRef:
                destination.SetResRef(label, (string)value);
                break;
            case GffFieldType.StrRef:
                destination.SetStrRef(label, (uint)value);
                break;
            case GffFieldType.Void:
                destination.SetVoid(label, (byte[])value);
                break;

            case GffFieldType.ExoLocString:
            {
                GffLocString text = (GffLocString)value;
                destination.SetLocString(label, text.StrRef, [.. text.Substrings]);
                break;
            }

            case GffFieldType.Vector:
            {
                float[] v = (float[])value;
                destination.SetVector(label, v[0], v[1], v[2]);
                break;
            }

            case GffFieldType.Orientation:
            {
                float[] q = (float[])value;
                destination.SetOrientation(label, q[0], q[1], q[2], q[3]);
                break;
            }

            case GffFieldType.Struct:
            {
                GffStruct child = (GffStruct)value;
                CopyInto(child, destination.AddStruct(label, child.StructId));
                break;
            }

            case GffFieldType.List:
            {
                GffWriterList list = destination.AddList(label);
                foreach (GffStruct member in (IReadOnlyList<GffStruct>)value)
                {
                    // The member's struct id is part of the data: the engine's own
                    // blueprints number list members meaningfully (0 for skills, 1 for
                    // feats), and a reader that switched on it would be handed the wrong
                    // shape if the copy renumbered them.
                    CopyInto(member, list.Add(member.StructId));
                }

                break;
            }

            default:
                throw new InvalidDataException($"GFF copy: field '{label}' has unknown type {(int)type}");
        }
    }
}
