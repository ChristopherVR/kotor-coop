"""Cairn Sentinel: original articulated basalt plates and a cyan resonator.
No reference images, extracted geometry, animal likeness or third-party character assets.
Legacy resource names are retained so installed appearance rows remain compatible.
"""
from __future__ import annotations
import numpy as np
import kmdl

NAME = "k_coop_chungus"
TEXTURE = NAME
GAUNTLET_TEXTURE = NAME + "g"  # legacy texture slot; now the cyan mineral seams
SCALE = 1.0

class Built:
    pass

def prism(radius, height, sides=6, taper=0.8):
    verts = []
    for z, r in ((-height/2, radius), (height/2, radius*taper)):
        verts.extend((r*np.cos(2*np.pi*i/sides), r*np.sin(2*np.pi*i/sides), z) for i in range(sides))
    faces = []
    for i in range(sides):
        j = (i+1)%sides
        faces.extend(((i,j,sides+j),(i,sides+j,sides+i)))
    for i in range(1,sides-1):
        faces.extend(((0,i+1,i),(sides,sides+i,sides+i+1)))
    return np.asarray(verts), faces

def mesh(radius, height, colour, sides=6, taper=0.8, stretch=(1,1,1), glow=False):
    verts, faces = prism(radius,height,sides,taper)
    verts *= stretch
    outv=[]; normals=[]; uvs=[]; outf=[]
    for f in faces:
        tri=verts[list(f)]; n=np.cross(tri[1]-tri[0],tri[2]-tri[0]); n/=np.linalg.norm(n)
        start=len(outv); outv.extend(map(tuple,tri)); normals.extend([tuple(n)]*3)
        uvs.extend([(0.05,0.05),(0.95,0.05),(0.5,0.95)]); outf.append((start,start+1,start+2))
    return kmdl.Mesh(outv,normals,uvs,outf,GAUNTLET_TEXTURE if glow else TEXTURE,
        diffuse=colour,selfillum=(0.35,0.8,0.75) if glow else (0.10,0.12,0.14),shadow=True)

def build(body_texture=1024, gauntlet_texture=512, log=print):
    b=Built(); b.root=kmdl.Node(NAME); b.joints={}; b.txi={GAUNTLET_TEXTURE:"envmaptexture CM_Baremetal\r\n"}
    yy,xx=np.mgrid[:body_texture,:body_texture]
    grain=((xx*17+yy*31)%23)/23
    b.textures={TEXTURE:np.stack([55+grain*16,69+grain*18,79+grain*20],axis=-1).astype('uint8')}
    seam=np.empty((gauntlet_texture,gauntlet_texture,4),dtype='uint8');seam[:]=[45,220,199,220]
    b.textures[GAUNTLET_TEXTURE]=seam
    cut=b.root.add(kmdl.Node('cutscenedummy')); root=cut.add(kmdl.Node('rootdummy',position=(0,0,1.1)))
    def part(parent,name,pos,r,h,colour=(1,1,1),**kw):
        return parent.add(kmdl.Node(name,position=pos,mesh=mesh(r,h,colour,**kw)))
    # Empty space between the plates and a single luminous core define the silhouette.
    part(root,'lower_shell',(0,0,-0.18),0.63,0.55,sides=5,taper=0.85)
    chest=part(root,'chest',(0,0,0.48),0.80,0.65,sides=5,taper=0.72,stretch=(1,0.65,1))
    part(chest,'resonator',(0,0.47,0.02),0.22,0.35,sides=3,taper=0.4,glow=True)
    head=part(chest,'head',(0,0,0.68),0.38,0.55,sides=3,taper=0.15,stretch=(1,0.8,1))
    part(head,'sight',(0,0.27,0),0.045,0.32,sides=4,taper=0.5,glow=True)
    # Unequal stone shoulder shelves, with two blunt three-prong tools; no fingers or glove.
    for side,tag in ((-1,'l'),(1,'r')):
        arm=part(chest,'arm'+tag,(side*0.93,0,-0.12),0.31,0.7,sides=5,taper=0.70)
        fore=part(arm,'fore'+tag,(side*0.12,0.14,-0.56),0.26,0.50,sides=4,taper=1.15)
        for j in (-1,0,1):part(fore,'prong'+tag+str(j+1),(j*0.14,0.05,-0.40),0.075,0.36,sides=4,taper=0.4)
        part(arm,'seam'+tag,(0,0.30,0.04),0.05,0.43,sides=4,glow=True)
        leg=part(root,'leg'+tag,(side*0.43,0,-0.63),0.24,0.65,sides=5,taper=0.85)
        part(leg,'foot'+tag,(0,0.14,-0.35),0.31,0.22,sides=4,taper=0.9,stretch=(1,1.35,1))
    for name,pos in [('impact',(0,0,0.4)),('headhook',(0,0,1.5)),('talkdummy',(0,0.3,1.2)),
                     ('headconjure',(0,0.3,1.5)),('lhand',(-1,0.3,-0.2)),('rhand',(1,0.3,-0.2)),('handconjure',(-1,0.4,0))]:
        root.add(kmdl.Node(name,position=pos))
    cut.add(kmdl.Node('camerahook',position=(0,0,2.3)))
    log('Built Cairn Sentinel from original procedural plate geometry.')
    return b
