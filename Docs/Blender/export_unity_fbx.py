"""Prepare the finished Blender temple for Unity and export a Unity-ready FBX.

Run headless:
  blender.exe -b scenes/sparkcode_temple.blend -P scripts/export_unity_fbx.py

- adds simple world-space box-projected UVs (the Unity marble/star shaders expect UVs)
- exports only MESH objects, excluding the camera-only sky dome and the volume fog box
- Z-up -> Y-up, metres, no animation
"""
import bpy
import os
from mathutils import Vector

OUT = r"D:\unityproject\epic\Assets\Art\Models\SparkCodeTemple.fbx"
os.makedirs(os.path.dirname(OUT), exist_ok=True)

SKIP = {"Sky_Dome", "Sky_SunGlow", "Sky_SunCore", "Fog_Domain"}


def box_uv(ob):
    """Cheap world-space box projection so the URP tiled materials show their texture."""
    me = ob.data
    if me is None or len(me.polygons) == 0:
        return
    if not me.uv_layers:
        me.uv_layers.new(name="UVMap")
    uv = me.uv_layers.active.data
    mw = ob.matrix_world
    n = ob.name.lower()
    if "floor" in n:
        tile = 4.0
    elif "star" in n or "spark" in n or "ribbon" in n or "dust" in n or "mini" in n:
        tile = 1.0
    elif "water" in n or "pool" in n:
        tile = 3.0
    else:
        tile = 2.0

    for poly in me.polygons:
        nor = poly.normal
        ax = max(range(3), key=lambda i: abs(nor[i]))
        for li in poly.loop_indices:
            co = mw @ me.vertices[me.loops[li].vertex_index].co
            if ax == 0:
                u, v = co.y, co.z
            elif ax == 1:
                u, v = co.x, co.z
            else:
                u, v = co.x, co.y
            uv[li].uv = (u / tile, v / tile)


meshes = [o for o in bpy.context.scene.objects if o.type == "MESH" and o.name not in SKIP]
for o in meshes:
    box_uv(o)

bpy.ops.object.select_all(action="DESELECT")
for o in meshes:
    o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]

# overall bounds for a sanity check
mn = Vector((1e9, 1e9, 1e9))
mx = Vector((-1e9, -1e9, -1e9))
for o in meshes:
    for c in o.bound_box:
        w = o.matrix_world @ Vector(c)
        mn = Vector((min(mn[i], w[i]) for i in range(3)))
        mx = Vector((max(mx[i], w[i]) for i in range(3)))
print("EXPORT_MESHES", len(meshes))
print("EXPORT_BOUNDS", tuple(round(v, 3) for v in mn), tuple(round(v, 3) for v in mx))

bpy.ops.export_scene.fbx(
    filepath=OUT,
    use_selection=True,
    apply_unit_scale=True,
    apply_scale_options="FBX_SCALE_ALL",
    global_scale=1.0,
    axis_forward="-Z",
    axis_up="Y",
    object_types={"MESH"},
    use_mesh_modifiers=True,
    mesh_smooth_type="FACE",
    use_tspace=False,
    add_leaf_bones=False,
    bake_anim=False,
    path_mode="AUTO",
)
print("FBX_WRITTEN", OUT, os.path.getsize(OUT))
