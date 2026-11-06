"""
Builds the classical temple scene as real Blender geometry and exports it for Unity.
Run:  D:\\blender\\blender.exe --background --python Tools/blender/build_temple.py
Everything is authored in metres, Z-up, then exported as FBX (Y-up for Unity).
"""
import bpy
import bmesh
import math
import os

ROOT = r"D:\unityproject\epic"
OUT_DIR = os.path.join(ROOT, "Assets", "Art", "Models")
BLEND = os.path.join(OUT_DIR, "Temple.blend")
FBX = os.path.join(OUT_DIR, "Temple.fbx")
SHOTS = os.path.join(ROOT, "Screenshots")
os.makedirs(OUT_DIR, exist_ok=True)
os.makedirs(SHOTS, exist_ok=True)

# ------------------------------------------------------------------------------- layout
PLAZA_R = 22.0
POOL_OUT = 7.2
POOL_IN = 3.3
WATER_Z = -0.35
BASIN_Z = -1.7
RIM_IN = 6.55
RIM_OUT = 7.35
RIM_TOP = 0.28
ISLAND_TOP = 0.55
INNER_RING = 10.4
INNER_COUNT = 16
INNER_H = 10.6
OUTER_RING = 18.0
OUTER_COUNT = 24
OUTER_H = 15.6
DOME_BASE_Z = 11.9
DOME_TOP_Z = 24.5
DOME_BASE_R = 10.1
OCULUS_R = 2.35
CEIL_Z = 16.2
WALL_R_IN = 20.95
WALL_R_OUT = 21.85
WALL_H = 17.4
DOOR_HALF = 6.5
DOOR_H = 9.5

# ------------------------------------------------------------------------------- helpers
def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.unit_settings.system = 'METRIC'
    bpy.context.scene.unit_settings.scale_length = 1.0


def collection(name):
    c = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(c)
    return c


def mat(name, color, metallic=0.0, rough=0.45, emission=None):
    m = bpy.data.materials.get(name)
    if m:
        return m
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = rough
    if emission is not None:
        try:
            bsdf.inputs["Emission Color"].default_value = (*emission, 1.0)
            bsdf.inputs["Emission Strength"].default_value = 2.0
        except Exception:
            pass
    m.diffuse_color = (*color, 1.0)
    return m


def obj_from_mesh(name, me, material, coll):
    ob = bpy.data.objects.new(name, me)
    coll.objects.link(ob)
    if material is not None and len(me.materials) == 0:
        me.materials.append(material)
    return ob


def assign_uvs(me, mode='cyl'):
    while me.uv_layers:
        me.uv_layers.remove(me.uv_layers[0])
    uv = me.uv_layers.new(name="UVMap")
    for poly in me.polygons:
        n = poly.normal
        for li in poly.loop_indices:
            co = me.vertices[me.loops[li].vertex_index].co
            if mode == 'planar':
                u, v = co.x / 6.0, co.y / 6.0
            elif mode == 'box':
                ax, ay, az = abs(n.x), abs(n.y), abs(n.z)
                if az >= ax and az >= ay:
                    u, v = co.x / 4.0, co.y / 4.0
                elif ax >= ay:
                    u, v = co.y / 4.0, co.z / 4.0
                else:
                    u, v = co.x / 4.0, co.z / 4.0
            else:
                u = math.atan2(co.y, co.x) / math.tau * 6.0
                v = co.z / 2.5
            uv.data[li].uv = (u, v)


def spin_closed(name, profile, material, coll, segments=96, uv_mode='cyl'):
    """Surface of revolution from a closed (radius, z) polygon."""
    bm = bmesh.new()
    vs = [bm.verts.new((r, 0.0, z)) for (r, z) in profile]
    bm.verts.ensure_lookup_table()
    for i in range(len(vs)):
        bm.edges.new((vs[i], vs[(i + 1) % len(vs)]))
    bm.edges.ensure_lookup_table()
    geom = list(bm.verts) + list(bm.edges)
    bmesh.ops.spin(bm, geom=geom, angle=math.tau, steps=segments,
                   axis=(0, 0, 1), cent=(0, 0, 0), use_duplicate=False)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.update()
    assign_uvs(me, uv_mode)
    return obj_from_mesh(name, me, material, coll)


def box(name, center, size, material, coll, rot_z=0.0):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bm.verts.ensure_lookup_table()
    sx, sy, sz = size
    for v in bm.verts:
        v.co.x *= sx
        v.co.y *= sy
        v.co.z *= sz
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.update()
    assign_uvs(me, 'box')
    ob = obj_from_mesh(name, me, material, coll)
    ob.location = center
    ob.rotation_euler = (0.0, 0.0, rot_z)
    return ob


def torus(name, major, minor, z, material, coll, seg_major=128, seg_minor=8):
    prof = []
    for i in range(seg_minor):
        a = math.tau * i / seg_minor
        prof.append((major + minor * math.cos(a), z + minor * math.sin(a)))
    return spin_closed(name, prof, material, coll, segments=seg_major)


def flat_ring(name, r_in, r_out, z, material, coll, segments=160):
    verts = []
    for i in range(segments):
        a = math.tau * i / segments
        verts.append((math.cos(a) * r_in, math.sin(a) * r_in, z))
    for i in range(segments):
        a = math.tau * i / segments
        verts.append((math.cos(a) * r_out, math.sin(a) * r_out, z))
    faces = []
    for i in range(segments):
        j = (i + 1) % segments
        faces.append((i, j, segments + j, segments + i))
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.validate()
    me.update()
    assign_uvs(me, 'planar')
    return obj_from_mesh(name, me, material, coll)


def cyl(name, radius, z0, z1, material, coll, segments=96, r_top=None):
    rt = radius if r_top is None else r_top
    prof = [(0.0, z0), (radius, z0), (rt, z1), (0.0, z1)]
    return spin_closed(name, prof, material, coll, segments=segments)


def apply_boolean(target, cutter, operation='DIFFERENCE'):
    m = target.modifiers.new("bool", 'BOOLEAN')
    m.operation = operation
    m.object = cutter
    m.solver = 'EXACT'
    bpy.context.view_layer.objects.active = target
    bpy.ops.object.modifier_apply(modifier=m.name)


# ------------------------------------------------------------------------------- build
reset()
coll = collection("Temple")
m_marble = mat("Marble", (0.86, 0.84, 0.78), 0.0, 0.35)
m_floor = mat("MarbleFloor", (0.80, 0.77, 0.70), 0.0, 0.30)
m_trim = mat("MarbleTrim", (0.90, 0.88, 0.82), 0.0, 0.32)
m_dark = mat("MarbleDark", (0.25, 0.24, 0.23), 0.0, 0.55)
m_basin = mat("MarbleBasin", (0.36, 0.42, 0.42), 0.0, 0.55)
m_gold = mat("Gold", (0.80, 0.55, 0.22), 1.0, 0.25)
m_water = mat("Water", (0.10, 0.32, 0.34), 0.1, 0.05)

# ---- floor
spin_closed("Floor", [(7.35, -0.20), (PLAZA_R, -0.20), (PLAZA_R, 0.0), (7.35, 0.0)], m_floor, coll, 192, uv_mode='planar')
spin_closed("Floor_EdgeRing", [(20.55, -0.02), (21.2, -0.02), (21.2, 0.16), (20.55, 0.16)], m_trim, coll, 160)
for i, r in enumerate((8.6, 13.0, 17.5)):
    spin_closed("Floor_Inlay_%d" % i, [(r - 0.13, 0.0), (r + 0.13, 0.0), (r + 0.13, 0.012), (r - 0.13, 0.012)], m_trim, coll, 160)
    torus("Floor_Gold_%d" % i, r, 0.030, 0.02, m_gold, coll)
torus("Floor_GoldEdge", 20.55, 0.034, 0.02, m_gold, coll)

# ---- pool basin / water / rim
spin_closed("Pool_BasinFloor", [(POOL_IN, BASIN_Z), (7.35, BASIN_Z), (7.35, BASIN_Z + 0.10), (POOL_IN, BASIN_Z + 0.10)], m_basin, coll, 160)
spin_closed("Pool_OuterWall", [(7.2, BASIN_Z), (7.35, BASIN_Z), (7.35, WATER_Z), (7.2, WATER_Z)], m_basin, coll, 160)
spin_closed("Pool_Rim", [(RIM_IN, WATER_Z - 0.05), (RIM_OUT, WATER_Z - 0.05), (RIM_OUT, RIM_TOP), (RIM_IN, RIM_TOP)], m_trim, coll, 160)
flat_ring("Pool_Water", POOL_IN, POOL_OUT, WATER_Z, m_water, coll, 160)
torus("Pool_GoldOuter", RIM_OUT - 0.035, 0.035, RIM_TOP + 0.012, m_gold, coll)
torus("Pool_GoldInner", RIM_IN + 0.035, 0.030, RIM_TOP + 0.012, m_gold, coll)

# ---- island + altar
cyl("Island_Base", 3.3, BASIN_Z, 0.05, m_trim, coll, 128)
cyl("Island_Step1", 3.0, 0.05, 0.30, m_trim, coll, 128)
cyl("Island_Top", 2.7, 0.30, ISLAND_TOP, m_floor, coll, 128)
cyl("Island_Altar", 1.05, ISLAND_TOP, 0.90, m_marble, coll, 96, r_top=1.02)
torus("Island_Gold", 1.05, 0.045, 0.90, m_gold, coll)
torus("Island_TopGold", 2.64, 0.025, ISLAND_TOP + 0.006, m_gold, coll)

# ---- columns
def column_mesh(name, h):
    prof = [
        (0.0, 0.0), (0.072 * h, 0.0), (0.072 * h, 0.03 * h),
        (0.064 * h, 0.035 * h), (0.064 * h, 0.05 * h), (0.056 * h, 0.06 * h),
        (0.056 * h, 0.075 * h), (0.052 * h, 0.085 * h),
        (0.050 * h, 0.12 * h), (0.042 * h, 0.86 * h),
        (0.044 * h, 0.875 * h), (0.062 * h, 0.915 * h),
        (0.070 * h, 0.935 * h), (0.030 * h, 0.94 * h), (0.0, 0.94 * h),
    ]
    return spin_closed(name, prof, m_marble, coll, 64)

inner_col_mesh = column_mesh("ColumnInnerMesh", INNER_H)
# detach mesh/data from the scene object; it will be instanced
inner_mesh_data = inner_col_mesh.data
bpy.data.objects.remove(inner_col_mesh, do_unlink=True)
inner_mesh_data.name = "ColumnInnerMesh"

outer_col_mesh = column_mesh("ColumnOuterMesh", OUTER_H)
outer_mesh_data = outer_col_mesh.data
bpy.data.objects.remove(outer_col_mesh, do_unlink=True)
outer_mesh_data.name = "ColumnOuterMesh"

# abacus boxes (instances)
def abacus_mesh(name, h):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co.x *= 0.15 * h
        v.co.y *= 0.15 * h
        v.co.z *= 0.075 * h
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.update()
    return me

inner_abacus = abacus_mesh("InnerAbacusMesh", INNER_H)
outer_abacus = abacus_mesh("OuterAbacusMesh", OUTER_H)

for i in range(INNER_COUNT):
    a = math.tau * i / INNER_COUNT
    x, y = math.cos(a) * INNER_RING, math.sin(a) * INNER_RING
    ob = bpy.data.objects.new("InnerColumn_%02d" % i, inner_mesh_data)
    coll.objects.link(ob)
    ob.location = (x, y, 0.0)
    ab = bpy.data.objects.new("InnerAbacus_%02d" % i, inner_abacus)
    coll.objects.link(ab)
    ab.location = (x, y, 0.9625 * INNER_H)
    ab.rotation_euler = (0.0, 0.0, a)

for i in range(OUTER_COUNT):
    a = math.tau * i / OUTER_COUNT
    x, y = math.cos(a) * OUTER_RING, math.sin(a) * OUTER_RING
    ob = bpy.data.objects.new("OuterColumn_%02d" % i, outer_mesh_data)
    coll.objects.link(ob)
    ob.location = (x, y, 0.0)
    ab = bpy.data.objects.new("OuterAbacus_%02d" % i, outer_abacus)
    coll.objects.link(ab)
    ab.location = (x, y, 0.9625 * OUTER_H)
    ab.rotation_euler = (0.0, 0.0, a)

# ---- inner entablature
spin_closed("InnerArchitrave", [(9.95, INNER_H), (10.9, INNER_H), (10.9, 10.98), (9.95, 10.98)], m_marble, coll, 192)
spin_closed("InnerFrieze", [(9.88, 10.98), (10.8, 10.98), (10.8, 11.45), (9.88, 11.45)], m_marble, coll, 192)
spin_closed("InnerCornice", [(9.68, 11.45), (11.05, 11.45), (11.05, 11.90), (9.68, 11.90)], m_marble, coll, 192)
torus("InnerGoldLow", 10.9, 0.025, INNER_H + 0.012, m_gold, coll)
torus("InnerGoldHigh", 11.02, 0.022, 11.445, m_gold, coll)
# dentils
for i in range(64):
    a = math.tau * i / 64
    box("InnerDentil_%02d" % i, (math.cos(a) * 10.86, math.sin(a) * 10.86, 11.52),
        (0.18, 0.16, 0.22), m_marble, coll, rot_z=a)

# ---- outer entablature (beside ceiling; the columns reach 15.6)
spin_closed("OuterArchitrave", [(17.3, OUTER_H), (18.95, OUTER_H), (18.95, 15.83), (17.3, 15.83)], m_marble, coll, 192)
spin_closed("OuterFrieze", [(17.22, 15.83), (18.85, 15.83), (18.85, 16.04), (17.22, 16.04)], m_marble, coll, 192)
spin_closed("OuterCornice", [(17.05, 16.04), (19.15, 16.04), (19.15, CEIL_Z), (17.05, CEIL_Z)], m_marble, coll, 192)
torus("OuterGoldLow", 18.93, 0.025, OUTER_H + 0.012, m_gold, coll)
torus("OuterGoldHigh", 19.1, 0.022, 16.035, m_gold, coll)
for i in range(80):
    a = math.tau * i / 80
    box("OuterDentil_%02d" % i, (math.cos(a) * 18.9, math.sin(a) * 18.9, 16.08),
        (0.17, 0.15, 0.20), m_marble, coll, rot_z=a)

# ---- perimeter wall with four real door openings (boolean)
wall = spin_closed("Walls", [(WALL_R_IN, 0.0), (WALL_R_OUT, 0.0), (WALL_R_OUT, WALL_H), (WALL_R_IN, WALL_H)], m_marble, coll, 192)
for i in range(4):
    a = math.tau * i / 4
    c = box("DoorCutter_%d" % i, (math.cos(a) * 21.4, math.sin(a) * 21.4, DOOR_H * 0.5),
            (4.9, 2.4, DOOR_H + 0.2), m_marble, coll, rot_z=a)
    apply_boolean(wall, c)
    bpy.data.objects.remove(c, do_unlink=True)
assign_uvs(wall.data, 'cyl')
# wall crown
spin_closed("WallCrown", [(20.8, 17.0), (22.0, 17.0), (22.0, 17.4), (20.8, 17.4)], m_marble, coll, 192)
torus("WallGoldLine", 20.96, 0.028, 17.02, m_gold, coll)

# pilasters (skip near doors)
for i in range(OUTER_COUNT):
    deg = i * 360.0 / OUTER_COUNT
    if min(abs(((deg - d + 180) % 360) - 180) for d in (0, 90, 180, 270)) < DOOR_HALF + 12:
        continue
    a = math.radians(deg)
    x, y = math.cos(a) * 20.78, math.sin(a) * 20.78
    box("Pilaster_%02d" % i, (x, y, 8.0), (0.62, 0.32, 15.4), m_trim, coll, rot_z=a)
    box("PilasterCap_%02d" % i, (math.cos(a) * 20.70, math.sin(a) * 20.70, 15.55), (0.86, 0.48, 0.42), m_trim, coll, rot_z=a)
    box("PilasterBase_%02d" % i, (math.cos(a) * 20.72, math.sin(a) * 20.72, 0.55), (0.82, 0.44, 0.55), m_trim, coll, rot_z=a)

# door frames + thresholds + gold strips
for i in range(4):
    deg = i * 90.0
    a = math.radians(deg)
    x, y = math.cos(a) * 21.15, math.sin(a) * 21.15
    box("DoorThreshold_%d" % i, (x, y, 0.06), (4.7, 1.0, 0.12), m_trim, coll, rot_z=a)
    box("DoorLintel_%d" % i, (math.cos(a) * 20.92, math.sin(a) * 20.92, DOOR_H + 0.20),
        (4.7, 0.25, 0.42), m_trim, coll, rot_z=a)
    box("DoorGoldLintel_%d" % i, (math.cos(a) * 20.79, math.sin(a) * 20.79, DOOR_H + 0.02),
        (4.35, 0.06, 0.07), m_gold, coll, rot_z=a)
    for s in (-1, 1):
        ang = a + s * math.radians(DOOR_HALF - 0.35)
        box("DoorJamb_%d_%d" % (i, s), (math.cos(ang) * 20.92, math.sin(ang) * 20.92, DOOR_H * 0.5),
            (0.36, 0.22, DOOR_H), m_trim, coll, rot_z=ang)
        box("DoorGoldJamb_%d_%d" % (i, s), (math.cos(ang) * 20.80, math.sin(ang) * 20.80, DOOR_H * 0.5),
            (0.06, 0.06, DOOR_H), m_gold, coll, rot_z=ang)

# ---- ceiling
spin_closed("Ceiling", [(7.62, CEIL_Z), (WALL_R_OUT, CEIL_Z), (WALL_R_OUT, CEIL_Z + 0.35), (7.62, CEIL_Z + 0.35)], m_dark, coll, 200, uv_mode='planar')
for i in range(24):
    a = math.tau * i / 24
    box("CeilBeam_%02d" % i, (math.cos(a) * 14.7, math.sin(a) * 14.7, 16.02),
        (13.6, 0.34, 0.36), m_dark, coll, rot_z=a)
for i, r in enumerate((10.2, 13.0, 16.0, 19.0)):
    spin_closed("CeilRing_%d" % i, [(r - 0.17, 15.84), (r + 0.17, 15.84), (r + 0.17, CEIL_Z), (r - 0.17, CEIL_Z)], m_dark, coll, 160)
    torus("CeilGold_%d" % i, r, 0.022, 15.85, m_gold, coll)
spin_closed("CeilInnerTrim", [(7.42, 15.62), (7.78, 15.62), (7.78, CEIL_Z), (7.42, CEIL_Z)], m_trim, coll, 160)
spin_closed("CeilOuterTrim", [(20.9, 15.55), (21.9, 15.55), (21.9, CEIL_Z), (20.9, CEIL_Z)], m_trim, coll, 192)
torus("CeilInnerGold", 7.6, 0.028, 15.63, m_gold, coll)

# ---- stepped dome: one watertight solid whose inner surface is the 12-step dome
steps = 12
inner_pts = [(DOME_BASE_R, DOME_BASE_Z)]
for k in range(steps):
    t0 = k / steps
    t1 = (k + 1) / steps
    r0 = DOME_BASE_R - 7.105 * t0 - 0.645 * t0 * t0
    r1 = DOME_BASE_R - 7.105 * t1 - 0.645 * t1 * t1
    z0 = DOME_BASE_Z + 12.6 * t0
    z1 = DOME_BASE_Z + 12.6 * t1
    inner_pts.append((r0, z1))       # riser
    inner_pts.append((r1, z1))       # soffit (faces down)
# close the top at the oculus and wrap around to a slightly larger exterior shell
profile = list(inner_pts)
profile.append((2.8, 25.0))           # oculus curb top
profile.append((2.8, 24.8))           # exterior top
for k in range(steps, -1, -1):
    t = k / steps
    r = DOME_BASE_R - 7.105 * t - 0.645 * t * t
    z = DOME_BASE_Z + 12.6 * t
    profile.append((r + 0.60, z))     # exterior shell
profile.append((DOME_BASE_R + 0.60, DOME_BASE_Z))
spin_closed("Dome", profile, m_marble, coll, 192)
# oculus ring + springing ring
torus("OculusGold", 2.42, 0.055, 24.52, m_gold, coll)
spin_closed("DomeSpringing", [(9.9, 11.72), (10.4, 11.72), (10.4, 11.95), (9.9, 11.95)], m_trim, coll, 192)
torus("DomeSpringGold", 10.2, 0.030, 11.94, m_gold, coll)

# ---- parent everything under one empty for Unity import
root = bpy.data.objects.new("Temple", None)
coll.objects.link(root)
for ob in list(coll.objects):
    if ob is root:
        continue
    if ob.parent is None:
        ob.parent = root
        ob.matrix_parent_inverse = root.matrix_world.inverted()

# ------------------------------------------------------------------------------- save + export
bpy.ops.wm.save_as_mainfile(filepath=BLEND)
bpy.ops.object.select_all(action='DESELECT')
root.select_set(True)
for ob in coll.objects:
    ob.select_set(True)
bpy.context.view_layer.objects.active = root
bpy.ops.export_scene.fbx(
    filepath=FBX,
    use_selection=True,
    apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_ALL',
    axis_forward='-Z',
    axis_up='Y',
    object_types={'EMPTY', 'MESH'},
    use_mesh_modifiers=True,
    mesh_smooth_type='FACE',
    add_leaf_bones=False,
    bake_anim=False,
    path_mode='AUTO',
)
print("FBX written:", FBX)

# ------------------------------------------------------------------------------- preview renders
def look_at(ob, target):
    c = ob.constraints.new('TRACK_TO')
    c.target = target
    c.track_axis = 'TRACK_NEGATIVE_Z'
    c.up_axis = 'UP_Y'

target = bpy.data.objects.new("LookTarget", None)
coll.objects.link(target)
target.location = (0, 0, 8.5)
cam_data = bpy.data.cameras.new("PreviewCam")
cam = bpy.data.objects.new("PreviewCam", cam_data)
coll.objects.link(cam)
look_at(cam, target)
bpy.context.scene.camera = cam
cam_data.lens = 38

sun_data = bpy.data.lights.new("Sun", 'SUN')
sun_data.energy = 4.5
sun = bpy.data.objects.new("Sun", sun_data)
coll.objects.link(sun)
sun.rotation_euler = (math.radians(38), math.radians(-18), math.radians(30))

# interior fill lights for the previews
area_data = bpy.data.lights.new("OculusFill", 'AREA')
area_data.energy = 2500.0
area_data.shape = 'DISK'
area_data.size = 7.0
area = bpy.data.objects.new("OculusFill", area_data)
coll.objects.link(area)
area.location = (0.0, 0.0, 23.5)

fill_data = bpy.data.lights.new("CameraFill", 'AREA')
fill_data.energy = 500.0
fill_data.shape = 'DISK'
fill_data.size = 6.0
fill = bpy.data.objects.new("CameraFill", fill_data)
coll.objects.link(fill)
fill.location = (0.0, -8.0, 6.5)
fill.rotation_euler = (math.radians(90), 0.0, 0.0)

world = bpy.data.worlds.new("World")
world.use_nodes = True
bg = world.node_tree.nodes.get("Background")
bg.inputs[0].default_value = (0.05, 0.07, 0.11, 1.0)
bg.inputs[1].default_value = 1.0
bpy.context.scene.world = world

scene = bpy.context.scene
scene.render.engine = 'CYCLES'
scene.cycles.device = 'CPU'
scene.cycles.samples = 32
scene.cycles.use_denoising = True
scene.render.resolution_x = 760
scene.render.resolution_y = 428
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'PNG'

cam.location = (0.0, -30.0, 7.0)
scene.render.filepath = os.path.join(SHOTS, "blender_ext.png")
bpy.ops.render.render(write_still=True)

cam.location = (0.0, -8.3, 2.6)
cam_data.lens = 24
scene.render.filepath = os.path.join(SHOTS, "blender_int.png")
bpy.ops.render.render(write_still=True)
print("PREVIEW DONE")
