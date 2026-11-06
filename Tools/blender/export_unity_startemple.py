"""Export the current Blender Star Temple as a Unity-friendly FBX.
Run with the finished .blend already loaded:
D:\\Blender\\blender.exe -b output\\星火神殿.blend --python export_unity_temple.py
The source .blend is never saved or modified on disk.
"""
import bpy
import json
from pathlib import Path

ROOT=Path(r'D:\dshproject\starttemple')
UNITY=Path(r'D:\unityproject\epic')
MODEL_DIR=UNITY/'Assets'/'Art'/'Models'
MODEL_DIR.mkdir(parents=True,exist_ok=True)
FBX=MODEL_DIR/'BlenderStarTemple.fbx'
REPORT=UNITY/'Docs'/'BlenderStarTemple_export.json'
REPORT.parent.mkdir(parents=True,exist_ok=True)

scene=bpy.context.scene
dg=bpy.context.evaluated_depsgraph_get()
export_collection=bpy.data.collections.new('UNITY_EXPORT_TEMP')
scene.collection.children.link(export_collection)
allowed=('01 |','02 |','03 |','04 |','07 |')
source=[]
for coll in scene.collection.children:
    if not coll.name.startswith(allowed):
        continue
    for ob in coll.objects:
        if ob.type not in {'MESH','CURVE'}:
            continue
        if 'atmospheric volume' in ob.name.lower() or 'volumetric light shaft' in ob.name.lower():
            continue
        source.append((ob,coll.name))

exported=[]
for index,(src,coll_name) in enumerate(source):
    evaluated=src.evaluated_get(dg)
    try:
        me=bpy.data.meshes.new_from_object(evaluated,preserve_all_data_layers=True,depsgraph=dg)
    except Exception as exc:
        print('SKIP',src.name,repr(exc))
        continue
    if me is None or not me.polygons:
        if me is not None:
            bpy.data.meshes.remove(me)
        continue
    me.update()
    # Simple per-face box UVs are robust for the procedurally generated custom
    # meshes, and keep the project's marble/water shaders usable in Unity.
    while me.uv_layers:
        me.uv_layers.remove(me.uv_layers[0])
    uv=me.uv_layers.new(name='UVMap')
    scale=0.50 if coll_name.startswith('07 |') else 0.25
    for poly in me.polygons:
        n=poly.normal
        ax,ay,az=abs(n.x),abs(n.y),abs(n.z)
        for li in poly.loop_indices:
            co=me.vertices[me.loops[li].vertex_index].co
            if az>=ax and az>=ay:
                u,v=co.x,co.y
            elif ay>=ax:
                u,v=co.x,co.z
            else:
                u,v=co.y,co.z
            uv.data[li].uv=(u*scale,v*scale)
    ob=bpy.data.objects.new(src.name,me)
    export_collection.objects.link(ob)
    ob.matrix_world=src.matrix_world.copy()
    ob['source_collection']=coll_name
    ob['source_object']=src.name
    ob.select_set(True)
    exported.append({'name':src.name,'collection':coll_name,
                     'materials':[m.name if m else '' for m in me.materials],
                     'vertices':len(me.vertices),'polygons':len(me.polygons)})
    print('EXPORT_PREP',index+1,len(source),src.name,len(me.vertices),flush=True)

bpy.ops.object.select_all(action='DESELECT')
for ob in export_collection.objects:
    ob.select_set(True)
if export_collection.objects:
    bpy.context.view_layer.objects.active=export_collection.objects[0]

bpy.ops.export_scene.fbx(
    filepath=str(FBX),use_selection=True,
    apply_unit_scale=True,apply_scale_options='FBX_SCALE_ALL',
    axis_forward='-Z',axis_up='Y',object_types={'MESH'},
    use_mesh_modifiers=True,mesh_smooth_type='FACE',
    add_leaf_bones=False,bake_anim=False,path_mode='AUTO')
report={'fbx':str(FBX),'source_blend':bpy.data.filepath,'objects':exported,
        'object_count':len(exported),'vertices':sum(o['vertices'] for o in exported),
        'polygons':sum(o['polygons'] for o in exported),
        'axis_forward':'-Z','axis_up':'Y','uv':'per-face box mapping'}
REPORT.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print('UNITY_FBX_EXPORTED',json.dumps({k:v for k,v in report.items() if k!='objects'},ensure_ascii=False),flush=True)
