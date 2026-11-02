
import bpy
from pathlib import Path
fbx = Path(r"/Users/sika/Desktop/code/BoluoProjectNew/CustomSRP/Assets/GameMain/Environment/Models/SM_OakTree_03.fbx")
high_name = "LOD1"
low_name = "LOD2"
out_high = Path(r"/Users/sika/Desktop/code/BoluoProjectNew/CustomSRP/Assets/_scratch/tree_lod_rebuild/Oak03/high/Oak03_high.obj")
out_low_ref = Path(r"/Users/sika/Desktop/code/BoluoProjectNew/CustomSRP/Assets/_scratch/tree_lod_rebuild/Oak03/high/Oak03_oldlow.obj")

bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.fbx(filepath=str(fbx), automatic_bone_orientation=False)

def find(substr):
    for o in bpy.context.scene.objects:
        if o.type == 'MESH' and substr.lower() in o.name.lower():
            return o
    return None

high = find(high_name)
low = find(low_name)
if high is None:
    raise SystemExit('high mesh not found: ' + high_name)

# Split by material slots into Bark/Leaf objects for export
def export_split(obj, path: Path):
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    # Duplicate
    bpy.ops.object.duplicate()
    dup = bpy.context.view_layer.objects.active
    # Separate by material
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.separate(type='MATERIAL')
    bpy.ops.object.mode_set(mode='OBJECT')
    parts = [o for o in bpy.context.selected_objects if o.type == 'MESH']
    # Rename by slot order approx: first selected order unstable — sort by tri count / name
    parts = sorted(parts, key=lambda o: o.name)
    if len(parts) >= 1:
        parts[0].name = 'Bark'
        parts[0].data.name = 'Bark'
    if len(parts) >= 2:
        # Larger canopy usually leaf
        parts = sorted(parts, key=lambda o: len(o.data.polygons))
        parts[0].name = 'Bark'
        parts[0].data.name = 'Bark'
        parts[-1].name = 'Leaf'
        parts[-1].data.name = 'Leaf'
        parts = [parts[0], parts[-1]] if parts[0] != parts[-1] else [parts[0]]
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts:
        p.select_set(True)
    path.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.obj_export(
        filepath=str(path),
        export_selected_objects=True,
        export_materials=False,
        export_uv=True,
        export_normals=True,
        forward_axis='NEGATIVE_Z',
        up_axis='Y',
    )
    # cleanup dups
    bpy.ops.object.delete()

export_split(high, out_high)
targets = {'bark': 0, 'leaf': 0}
if low is not None:
    # count tris per material
    mesh = low.data
    if len(mesh.materials) >= 2:
        # approximate: use polygon material_index
        bark = sum(1 for p in mesh.polygons if p.material_index == 0)
        leaf = sum(1 for p in mesh.polygons if p.material_index == 1)
        targets = {'bark': bark, 'leaf': leaf}
    else:
        targets = {'bark': len(mesh.polygons)//3, 'leaf': len(mesh.polygons) - len(mesh.polygons)//3}
out_targets = Path(r"/Users/sika/Desktop/code/BoluoProjectNew/CustomSRP/Assets/_scratch/tree_lod_rebuild/Oak03/targets.json")
import json
out_targets.write_text(json.dumps(targets))
print('EXTRACT_OK', out_high, targets)
