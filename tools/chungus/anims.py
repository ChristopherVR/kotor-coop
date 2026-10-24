"""Original mechanical motion for Cairn Sentinel; engine animation names are compatibility IDs."""
import math
import kmdl
import geom
WALK_DISTANCE=2.0
RUN_DISTANCE=6.3
NAMES=('cpause1 cpause2 creadyr cwalk crun cwalkinj g0a1 g0a2 cdodgeg cdamages cdie cdead '
       'ckdbck ckdbcklp cgustandb ctaunt cvictory disabled usecomp chturnl chturnr tlknorm '
       'choke horror sleep paralyzed whirlwind cspasm').split()

def build(root):
    result=[]
    for name in NAMES:
        duration=1.2 if name in ('cwalk','crun','cwalkinj') else 2.4
        tracks={}
        def rot(node,rows):tracks[node]={'orientation':[(t,*geom.q_euler(x,y,z)) for t,x,y,z in rows]}
        if name in ('cwalk','crun','cwalkinj'):
            for tag,sign in (('l',1),('r',-1)):
                rot('leg'+tag,[(duration*k/8,sign*30*math.sin(k*math.pi/4),0,0) for k in range(9)])
                rot('arm'+tag,[(duration*k/8,-sign*18*math.sin(k*math.pi/4),0,0) for k in range(9)])
        elif name in ('cdie','cdead','ckdbck','ckdbcklp','sleep'):
            dead=name in ('cdead','ckdbcklp','sleep')
            rot('rootdummy',[(0,82 if dead else 0,0,0),(duration,82,0,0)])
            tracks['rootdummy']['position']=[(0,0,0,-0.7 if dead else 0),(duration,0,0,-0.7)]
        elif name in ('g0a1','g0a2','usecomp'):
            tag='l' if name=='g0a1' else 'r'
            rot('arm'+tag,[(0,0,0,0),(0.45,-85,0,0),(0.8,40,0,0),(duration,0,0,0)])
        elif name in ('ctaunt','cvictory','disabled','chturnl','chturnr'):
            # Chest resonance, both blunt arms braced outward. No finger-snap gesture.
            for tag,sign in (('l',1),('r',-1)):
                rot('arm'+tag,[(0,0,0,0),(0.7,-25,sign*45,0),(1.3,-25,sign*45,0),(duration,0,0,0)])
            tracks['resonator']={'selfillum':[(0,0.2,0.5,0.45),(1.0,0.6,1,0.9),(duration,0.2,0.5,0.45)]}
        else:
            rot('chest',[(0,0,0,-2),(duration/2,0,0,2),(duration,0,0,-2)])
        events=[(0.8,'Hit')] if name in ('g0a1','g0a2') else []
        result.append(kmdl.Animation(name,duration,0.25,events,tracks))
    return result
