"""Create editable Blueprints from a UEBulkExport reconstruction plan.

Requires UE 5.8 with the Python Editor Script Plugin. A schema 1 plan creates
one Blueprint. A schema 2 plan creates a group ordered by dependencies. The
destination defaults to /Game/RecoveredBlueprints. Existing assets are kept.

Use --plan, --destination, and --report, or set UEBULKEXPORT_BLUEPRINT_PLAN,
UEBULKEXPORT_BLUEPRINT_DESTINATION, and UEBULKEXPORT_BLUEPRINT_REPORT before
launch. The optional report must be a new file in an existing directory. For mapped
StaticMesh references, use --mesh-map or UEBULKEXPORT_BLUEPRINT_MESH_MAP
with a schema 1 manifest of original and existing Unreal object paths. The
automatic dump passes its prepared glTF geometry and socket data through
UEBULKEXPORT_BLUEPRINT_MESH_IMPORTS.
"""

import argparse
from hashlib import sha256
from contextlib import contextmanager, nullcontext
import json
import math
import os
from pathlib import Path
import re

import unreal


IDENTIFIER = re.compile(r"[A-Za-z_][A-Za-z0-9_]*\Z")
ACTOR_CLASS = "/Script/Engine.Actor"
LIBRARY_CLASS = "/Script/Engine.BlueprintFunctionLibrary"
SCENE_COMPONENT_CLASS = "/Script/Engine.SceneComponent"
STATIC_MESH_COMPONENT_CLASS = "/Script/Engine.StaticMeshComponent"
SCENE_CLASSES = (SCENE_COMPONENT_CLASS, STATIC_MESH_COMPONENT_CLASS)
OBJECT_PATH = re.compile(r"/(?:[A-Za-z0-9_]+/)+([A-Za-z0-9_]+)\.\1\Z")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def filesystem_path(value):
    path = Path(value)
    if os.name != "nt":
        return path
    absolute = os.path.abspath(path)
    # The embedded interpreter can run under a host that enforces MAX_PATH.
    if absolute.startswith(("\\\\?\\", "\\\\.\\")):
        return Path(absolute)
    if absolute.startswith("\\\\"):
        return Path("\\\\?\\UNC\\" + absolute[2:])
    return Path("\\\\?\\" + absolute)


def validate_name(value, kind):
    require(isinstance(value, str) and IDENTIFIER.fullmatch(value) and value.lower() != "none",
            f"Unsupported {kind} name: {value!r}.")
    return value


def validate_type(value, context):
    require(value in ("int", "bool"), f"Unsupported type in {context}: {value!r}.")
    return value


def validate_value(value, data_type, context):
    if data_type == "int":
        require(type(value) is int and -(2 ** 31) <= value < 2 ** 31,
                f"{context} must be a signed 32-bit integer.")
    else:
        require(type(value) is bool, f"{context} must be a Boolean.")


def validate_seq(value, context):
    require(isinstance(value, list), f"{context} must be an array.")
    require(all(isinstance(item, dict) for item in value), f"{context} must contain objects.")
    return value


def validate_member(member, context):
    return validate_name(member.get("name"), context), validate_type(member.get("type"), context)


def add_unique(names, name, context):
    key = name.casefold()
    require(key not in names, f"Duplicate {context}: {name}.")
    names.add(key)


def validate_call(body, inputs, outputs, prior, context):
    require(len(inputs) == 1 and len(outputs) == 1 and outputs[0]["name"] == "ReturnValue",
            f"A recovered call requires one input and one ReturnValue in {context}.")
    args = validate_seq(body.get("arguments"), f"{context}.arguments")
    require(len(args) == 1 and args[0].get("parameter") == inputs[0]["name"],
            f"The recovered call must use the declared input in {context}.")
    target_name = body.get("targetClass")
    require(isinstance(target_name, str), f"A cooked target class is required in {context}.")
    target = prior.get(target_name)
    require(target and target["parentClass"] == LIBRARY_CLASS,
            f"The target of {context} must be a preceding Blueprint Function Library in the plan.")
    function_name = validate_name(body.get("targetFunction"), "called function")
    function = next((item for item in target["functions"] if item["name"] == function_name), None)
    require(function and function.get("pure", False) and function["body"]["kind"] != "stub",
            f"The target of {context} must be a recovered pure function in {target['name']}.")
    require(len(function["inputs"]) == 1 and function["inputs"][0]["type"] == inputs[0]["type"] and
            len(function["outputs"]) == 1 and function["outputs"][0]["name"] == "ReturnValue" and
            function["outputs"][0]["type"] == outputs[0]["type"],
            f"The target of {context} has an incompatible signature.")


def validate_property(body, variables, context):
    property_name = validate_name(body.get("property"), "property")
    require(property_name in variables, f"Unknown recovered instance property {property_name} in {context}.")
    return variables[property_name]


def validate_property_write(body, variables, inputs, context):
    data_type = validate_property(body, variables, context)
    if "parameter" in body:
        require("value" not in body and len(inputs) == 1 and inputs[0]["name"] == body["parameter"] and
                inputs[0]["type"] == data_type, f"The property write requires a matching scalar parameter in {context}.")
    else:
        require(not inputs, f"A literal property write requires no inputs in {context}.")
        validate_value(body.get("value"), data_type, f"Property value of {context}")


def validate_instance_call(body, inputs, outputs, prior, context):
    require(len(inputs) == 1 and inputs[0]["type"] == "object" and inputs[0]["name"] == body.get("receiver") and
            len(outputs) == 1 and outputs[0]["name"] == "ReturnValue" and body.get("arguments") == [],
            f"A recovered instance call requires one typed receiver and one scalar ReturnValue in {context}.")
    target_name = body.get("targetClass")
    require(isinstance(target_name, str) and inputs[0].get("typeReference") == target_name,
            f"The receiver type does not match the target class in {context}.")
    target = prior.get(target_name)
    require(target and target["parentClass"] != LIBRARY_CLASS,
            f"The receiver must use a preceding Actor Blueprint in {context}.")
    function_name = validate_name(body.get("targetFunction"), "called function")
    function = next((item for item in target["functions"] if item["name"] == function_name), None)
    require(function and function.get("pure", False) and function["body"]["kind"] in ("constant-return", "property-read"),
            f"The target of {context} must be a recovered pure scalar getter in {target['name']}.")
    require(not function["inputs"] and len(function["outputs"]) == 1 and
            function["outputs"][0]["name"] == "ReturnValue" and function["outputs"][0]["type"] == outputs[0]["type"],
            f"The target of {context} has an incompatible signature.")


def validate_stub(body, context):
    require(body.get("kind") == "stub" and isinstance(body.get("reason"), str) and body["reason"].strip(),
            f"An unsupported body must be an explicit stub with a reason in {context}.")


def ancestor_members(parent, prior):
    variables, functions = {}, {}
    while parent in prior:
        blueprint = prior[parent]
        for variable in blueprint["variables"]:
            variables.setdefault(variable["name"], variable["type"])
        for function in blueprint["functions"]:
            functions.setdefault(function["name"], (parent, function))
        parent = blueprint["parentClass"]
    return variables, functions


def validate_override(function, inherited, context):
    declared = inherited.get(function["name"])
    override = function.get("overrideClass")
    if not declared and override is None:
        return
    require(declared and override == declared[0], f"The override does not name its declaring ancestor in {context}.")
    base = declared[1]
    require(base.get("pure", False) and function.get("pure", False) and
            base["inputs"] == function["inputs"] == [] and base["outputs"] == function["outputs"] and
            len(function["outputs"]) == 1 and function["outputs"][0]["name"] == "ReturnValue" and
            function["body"].get("kind") in ("constant-return", "property-read"),
            f"The override requires a matching pure no-argument scalar function in {context}.")


TRANSFORMS = (
    ("relativeLocation", "relative_location", ("x", "y", "z")),
    ("relativeRotation", "relative_rotation", ("pitch", "yaw", "roll")),
    ("relativeScale", "relative_scale3d", ("x", "y", "z")),
)
COMPONENT_FLAGS = (("visible", "visible"), ("hiddenInGame", "hidden_in_game"))


def validate_asset_path(path, context):
    require(isinstance(path, str) and OBJECT_PATH.fullmatch(path), f"Invalid Unreal asset path in {context}: {path!r}.")
    return path


def validate_component_properties(component, name):
    for key, _, fields in TRANSFORMS:
        if key not in component:
            continue
        transform = component[key]
        require(isinstance(transform, dict) and set(transform) == set(fields) and
                all(type(value) in (int, float) and math.isfinite(value) for value in transform.values()),
                f"Invalid {key} for {name}.")
    for key, _ in COMPONENT_FLAGS:
        require(key not in component or type(component[key]) is bool, f"Invalid {key} for {name}.")
    if "staticMesh" in component:
        require(component["classPath"] == STATIC_MESH_COMPONENT_CLASS and "clearStaticMesh" not in component,
                f"A static mesh assignment requires a StaticMeshComponent in {name}.")
        validate_asset_path(component["staticMesh"], name)
    if "clearStaticMesh" in component:
        require(component["classPath"] == STATIC_MESH_COMPONENT_CLASS and component["clearStaticMesh"] is True,
                f"A static mesh clear must explicitly be true on a StaticMeshComponent in {name}.")


def validate_components(plan, parent, members, prior):
    components = validate_seq(plan.get("components", []), "components")
    require(not components or parent == ACTOR_CLASS, "Local scene trees require a native Actor parent.")
    names = {}
    for index, component in enumerate(components):
        name = validate_name(component.get("name"), "component")
        component_class = component.get("classPath")
        require(component_class in SCENE_CLASSES, f"Unsupported scene component class in {name}.")
        attachment = component.get("parent")
        require(attachment in names if isinstance(attachment, str) else attachment is None and index == 0,
                f"Component {name} must attach to a preceding component.")
        default_root = component.get("isDefaultSceneRoot", False)
        require(type(default_root) is bool and (not default_root or
                index == 0 and name == "DefaultSceneRoot" and component_class == SCENE_COMPONENT_CLASS),
                f"Invalid default scene root for {name}.")
        add_unique(members, name, "Blueprint member")
        validate_component_properties(component, name)
        if "socket" in component:
            socket = component["socket"]
            require(isinstance(socket, str) and socket.strip() and socket.isprintable() and
                    socket.casefold() != "none" and len(socket) <= 255 and isinstance(attachment, str) and
                    names[attachment]["classPath"] == STATIC_MESH_COMPONENT_CLASS,
                    f"Component {name} requires a named socket on a local StaticMeshComponent parent.")
        names[name] = component

    ancestors = {}
    while parent in prior:
        ancestor = prior[parent]
        ancestors[parent] = {entry["name"]: entry for entry in ancestor.get("components", [])}
        parent = ancestor["parentClass"]
    seen = set()
    for patch in validate_seq(plan.get("componentOverrides", []), "componentOverrides"):
        name = validate_name(patch.get("name"), "inherited component")
        owner = patch.get("ownerClass")
        template = ancestors.get(owner, {}).get(name) if isinstance(owner, str) else None
        require(template and patch.get("classPath") == template["classPath"],
                f"Component override {name} must identify its declaring ancestor and native component class.")
        key = (owner, name)
        require(key not in seen, f"Duplicate component override: {name}.")
        seen.add(key)
        require(not {"parent", "socket", "isDefaultSceneRoot"}.intersection(patch),
                f"Inherited component override {name} cannot change attachment topology.")
        validate_component_properties(patch, name)


def validate_blueprint(plan, prior):
    require(type(plan.get("schemaVersion")) is int and plan["schemaVersion"] == 1,
            "Each Blueprint must be a schemaVersion 1 plan.")
    name = validate_name(plan.get("name"), "Blueprint")
    parent = plan.get("parentClass")
    parent_blueprint = prior.get(parent) if isinstance(parent, str) else None
    require(parent in (ACTOR_CLASS, LIBRARY_CLASS) or parent_blueprint and parent_blueprint["parentClass"] != LIBRARY_CLASS,
            f"Unsupported or out-of-order parent class in {name}: {parent!r}.")
    source = plan.get("sourceAsset")
    require(isinstance(source, str) and source.strip(), "sourceAsset must identify the cooked source.")

    inherited_variables, inherited_functions = ancestor_members(parent, prior)
    members = {field.casefold() for field in inherited_variables}
    variable_types = dict(inherited_variables)
    validate_components(plan, parent, members, prior)
    variables = validate_seq(plan.get("variables"), f"{name}.variables")
    require(parent != LIBRARY_CLASS or not variables, f"A function library cannot have variables in {name}.")
    for variable in variables:
        variable_name, data_type = validate_member(variable, "variable")
        add_unique(members, variable_name, "Blueprint member")
        variable_types[variable_name] = data_type
        if "defaultValue" in variable:
            validate_value(variable["defaultValue"], data_type, f"Default for {variable_name}")

    defaults = set()
    for default in validate_seq(plan.get("inheritedDefaults", []), f"{name}.inheritedDefaults"):
        field, data_type = validate_member(default, "inherited variable default")
        add_unique(defaults, field, "inherited variable default")
        require(inherited_variables.get(field) == data_type, f"Unknown or incompatible inherited default {field} in {name}.")
        validate_value(default.get("value"), data_type, f"Inherited default for {field}")

    for function in validate_seq(plan.get("functions"), f"{name}.functions"):
        function_name = validate_name(function.get("name"), "function")
        context = f"{name}.{function_name}"
        add_unique(members, function_name, "Blueprint member")
        pure = function.get("pure", False)
        require(type(pure) is bool, f"pure must be a Boolean in {context}.")
        require(parent != LIBRARY_CLASS or pure, f"A recovered library function must be pure in {context}.")
        inputs = validate_seq(function.get("inputs"), f"{context}.inputs")
        outputs = validate_seq(function.get("outputs"), f"{context}.outputs")
        body = function.get("body")
        require(isinstance(body, dict), f"Function body declaration is required in {context}.")
        validate_override(function, inherited_functions, context)
        kind = body.get("kind")
        names = set()
        for parameter in inputs:
            if parameter.get("type") == "object":
                require(kind == "instance-call-return", f"Unexpected object input in {context}.")
                parameter_name = validate_name(parameter.get("name"), f"parameter of {context}")
            else:
                parameter_name, _ = validate_member(parameter, f"parameter of {context}")
            add_unique(names, parameter_name, f"parameter of {context}")
        for parameter in outputs:
            parameter_name, _ = validate_member(parameter, f"parameter of {context}")
            add_unique(names, parameter_name, f"parameter of {context}")
        if kind == "constant-return":
            require(not inputs and len(outputs) == 1 and outputs[0]["name"] == "ReturnValue",
                    f"A constant return requires no inputs and one ReturnValue output in {context}.")
            validate_value(body.get("value"), outputs[0]["type"], f"Return value of {context}")
        elif kind == "parameter-return":
            require(len(inputs) == 1 and len(outputs) == 1 and outputs[0]["name"] == "ReturnValue" and
                    inputs[0]["type"] == outputs[0]["type"] and inputs[0]["name"] == body.get("parameter"),
                    f"A parameter return requires one matching input and one same-type ReturnValue in {context}.")
        elif kind == "conditional-return":
            require(not pure and len(inputs) == 1 and inputs[0]["type"] == "bool" and
                    inputs[0]["name"] == body.get("condition") and len(outputs) == 1 and
                    outputs[0]["name"] == "ReturnValue",
                    f"A conditional return requires one Boolean input and one ReturnValue in {context}.")
            validate_value(body.get("whenTrue"), outputs[0]["type"], f"True return value of {context}")
            validate_value(body.get("whenFalse"), outputs[0]["type"], f"False return value of {context}")
        elif kind == "function-call-return":
            validate_call(body, inputs, outputs, prior, context)
        elif kind == "property-read":
            require(not inputs and len(outputs) == 1 and outputs[0]["name"] == "ReturnValue" and
                    outputs[0]["type"] == validate_property(body, variable_types, context),
                    f"A property read requires one same-type ReturnValue in {context}.")
        elif kind == "property-write":
            require(not pure and not outputs, f"A property write requires an impure function without outputs in {context}.")
            validate_property_write(body, variable_types, inputs, context)
        elif kind == "instance-call-return":
            validate_instance_call(body, inputs, outputs, prior, context)
        else:
            validate_stub(body, context)

    events = validate_seq(plan.get("events", []), f"{name}.events")
    require(parent != LIBRARY_CLASS or not events, f"Custom events require an Actor Blueprint in {name}.")
    for event in events:
        event_name = validate_name(event.get("name"), "custom event")
        add_unique(members, event_name, "Blueprint member")
        context = f"{name}.{event_name}"
        inputs = validate_seq(event.get("inputs"), f"{context}.inputs")
        require(len(inputs) <= 1, f"A recovered custom event may have one scalar input in {context}.")
        for parameter in inputs:
            validate_member(parameter, f"parameter of {context}")
        body = event.get("body")
        require(isinstance(body, dict), f"Event body declaration is required in {context}.")
        if body.get("kind") == "property-write":
            validate_property_write(body, variable_types, inputs, context)
        else:
            validate_stub(body, context)
    omissions = plan.get("omissions", [])
    require(isinstance(omissions, list) and all(isinstance(item, str) for item in omissions),
            "omissions must contain strings.")


def validate_plan(plan, destination):
    require(isinstance(plan, dict) and type(plan.get("schemaVersion")) is int and
            plan["schemaVersion"] in (1, 2), "Expected a schemaVersion 1 or 2 Blueprint plan.")
    parts = destination.rstrip("/").split("/")
    require(parts[:2] == ["", "Game"], "The destination must be /Game or a folder below it.")
    for part in parts[2:]:
        validate_name(part, "destination folder")

    blueprints = [plan] if plan["schemaVersion"] == 1 else validate_seq(plan.get("blueprints"), "blueprints")
    require(blueprints, "The Blueprint plan is empty.")
    paths = []
    names, sources, prior = set(), set(), {}
    for blueprint in blueprints:
        validate_blueprint(blueprint, prior)
        add_unique(names, blueprint["name"], "destination asset")
        add_unique(sources, blueprint["sourceAsset"], "cooked Blueprint")
        prior[blueprint["sourceAsset"]] = blueprint
        paths.append("/".join(parts) + "/" + blueprint["name"])
    return blueprints, paths


def pin_literal(value):
    if type(value) is bool:
        return "true" if value else "false"
    return str(value)


def connect(source, target, message):
    require(source.is_valid() and target.is_valid() and source.try_create_connection(target), message)


def add_property_write(graph, entry, parameters, body, context):
    lib = unreal.BlueprintEditorLibrary
    name = body["property"]
    setter = graph.add_set_member_variable_node(name)
    require(setter is not None, f"Could not create the property setter in {context}.")
    connect(entry, lib.find_execute_pin(setter), f"Could not execute the property setter in {context}.")
    pin = lib.find_input_pin(setter, name)
    position = lib.get_node_pos(entry.get_owning_node())
    lib.set_node_pos(setter, unreal.IntPoint(position.x + 360, position.y))
    if "parameter" in body:
        parameter = body["parameter"]
        connect(parameters[parameter], pin, f"Could not wire the property value in {context}.")
        return {"property": name, "parameter": parameter}
    value = body["value"]
    require(pin.is_valid() and pin.set_pin_value(pin_literal(value)), f"Could not set the property value in {context}.")
    return {"property": name, "value": value}


def read_mesh_map(path):
    if not path:
        return {}
    with filesystem_path(path).open("r", encoding="utf-8-sig") as stream:
        mapping = json.load(stream)
    require(isinstance(mapping, dict) and type(mapping.get("schemaVersion")) is int and
            mapping["schemaVersion"] == 1 and isinstance(mapping.get("meshes"), dict),
            "Expected a schemaVersion 1 mesh map with a meshes object.")
    for original, target in mapping["meshes"].items():
        validate_asset_path(original, "mesh map key")
        validate_asset_path(target, f"mesh map entry {original}")
    return mapping["meshes"]


MATERIAL_CHANNELS = (
    ("baseColorTexturePath", "baseColor", "MP_BASE_COLOR", "RGB"),
    ("normalTexturePath", None, "MP_NORMAL", "RGB"),
    ("roughnessTexturePath", "roughness", "MP_ROUGHNESS", "R"),
    ("metallicTexturePath", "metallic", "MP_METALLIC", "R"),
    ("occlusionTexturePath", None, "MP_AMBIENT_OCCLUSION", "R"),
    (None, "specular", "MP_SPECULAR", "R"),
)
COLLISION_SHAPES = ("boxes", "spheres", "capsules")
VECTOR_FIELDS = ("x", "y", "z")
ROTATION_FIELDS = ("pitch", "yaw", "roll")


def finite_number(value):
    return type(value) in (int, float) and math.isfinite(value)


def validate_coordinates(values, fields, context):
    require(isinstance(values, dict) and set(values) == set(fields) and
            all(finite_number(value) for value in values.values()), f"Invalid {context}.")


def prepared_file(directory, value, extensions, context):
    require(isinstance(value, str) and value, f"No file for {context}.")
    file = filesystem_path(directory / value).resolve()
    require(file.is_relative_to(directory) and file.suffix.lower() in extensions and file.is_file(),
            f"{context} must point to a prepared file inside the manifest workspace.")
    return file


def validate_collision(entry, context):
    for shape_name in COLLISION_SHAPES:
        for shape in validate_seq(entry.get(shape_name, []), f"{context}.{shape_name}"):
            validate_coordinates(shape.get("center"), VECTOR_FIELDS, f"{shape_name} center in {context}")
            if shape_name != "spheres":
                validate_coordinates(shape.get("rotation"), ROTATION_FIELDS, f"{shape_name} rotation in {context}")
            if shape_name == "boxes":
                dimensions = shape.get("dimensions")
                validate_coordinates(dimensions, VECTOR_FIELDS, f"box lengths in {context}")
                require(all(value > 0 for value in dimensions.values()), f"Box lengths must be positive in {context}.")
            else:
                radius = shape.get("radius")
                require(finite_number(radius) and radius > 0, f"Collision radius must be positive in {context}.")
                if shape_name == "capsules":
                    length = shape.get("length")
                    require(finite_number(length) and length >= 0, f"Capsule cylinder length is invalid in {context}.")


def read_prepared_mesh(entry, directory, original):
    geometry = prepared_file(directory, entry.get("geometryPath"), (".glb", ".gltf"), f"Mesh geometry for {original}")
    lods = validate_seq(entry.get("lods", []), f"{original}.lods")
    require(len(lods) <= 7, f"At most eight total LODs are supported in {original}.")
    prepared_lods = []
    for expected, lod in enumerate(lods, 1):
        index = lod.get("lodIndex")
        require(type(index) is int and index == expected, f"The additional LODs must be ordered from 1 without gaps in {original}.")
        file = prepared_file(directory, lod.get("geometryPath"), (".glb", ".gltf"), f"LOD {index} for {original}")
        prepared_lods.append((index, file))

    sockets = validate_seq(entry.get("sockets"), f"{original}.sockets")
    socket_names = set()
    for socket in sockets:
        name = socket.get("name")
        require(isinstance(name, str) and name.strip() and name.isprintable() and len(name) <= 255 and
                name.casefold() != "none", f"Invalid socket name in {original}: {name!r}.")
        add_unique(socket_names, name, "mesh socket")
        for key, _, fields in TRANSFORMS:
            if key in socket:
                validate_coordinates(socket[key], fields, f"{key} for socket {name} in {original}")
        require(socket.get("tag") is None or isinstance(socket["tag"], str), f"Invalid tag in socket {name} in {original}.")
    validate_collision(entry, original)

    slots = validate_seq(entry.get("materialSlots", []), f"{original}.materialSlots")
    slot_names = set()
    for expected, slot in enumerate(slots):
        index = slot.get("slotIndex")
        require(type(index) is int and index == expected, f"Material slots must be ordered from 0 without gaps in {original}.")
        name = slot.get("slotName")
        require(isinstance(name, str) and name.strip() and name.isprintable() and name.casefold() != "none",
                f"Invalid material slot name in {original}: {name!r}.")
        add_unique(slot_names, name, "material slot name")
        if slot.get("materialObjectPath") is not None:
            validate_asset_path(slot["materialObjectPath"], f"material slot {name} of {original}")
    return {
        "geometry": geometry,
        "lods": prepared_lods,
        "sockets": sockets,
        "materialSlots": slots,
        **{shape_name: entry.get(shape_name, []) for shape_name in COLLISION_SHAPES},
    }


def read_prepared_material(entry, original):
    for texture_field, scalar_field, _, _ in MATERIAL_CHANNELS:
        if texture_field and entry.get(texture_field) is not None:
            validate_asset_path(entry[texture_field], f"{texture_field} for {original}")
        if not scalar_field or entry.get(scalar_field) is None:
            continue
        value = entry[scalar_field]
        if scalar_field == "baseColor":
            validate_coordinates(value, ("r", "g", "b", "a"), f"base color for {original}")
        else:
            require(finite_number(value), f"Invalid {scalar_field} value for {original}.")
    return entry


def read_prepared_texture(entry, directory, original):
    path = prepared_file(directory, entry.get("filePath"), (".png",), f"Texture data for {original}")
    require(all(type(entry.get(field)) is int and entry[field] > 0 for field in ("width", "height")),
            f"Invalid dimensions for texture {original}.")
    require(type(entry.get("srgb")) is bool and type(entry.get("normalMap")) is bool and
            not (entry["srgb"] and entry["normalMap"]), f"Invalid color or normal map settings for {original}.")
    return {**entry, "file": path}


def read_mesh_imports(path):
    bundle = {"meshes": {}, "materials": {}, "textures": {}}
    if not path:
        return {**bundle, "warnings": set()}
    manifest = filesystem_path(path)
    with manifest.open("r", encoding="utf-8-sig") as stream:
        payload = json.load(stream)
    require(isinstance(payload, dict) and type(payload.get("schemaVersion")) is int and
            payload["schemaVersion"] == 1, "Expected a schemaVersion 1 mesh import manifest.")
    directory = manifest.parent.resolve(strict=True)
    known_sources = set()
    for field in bundle:
        for entry in validate_seq(payload.get(field, []), f"mesh import {field}"):
            original = validate_asset_path(entry.get("sourceObjectPath"), f"{field} import")
            require(original.startswith("/Game/"), f"Prepared asset must target writable /Game content: {original}.")
            add_unique(known_sources, original, "prepared asset")
            if field == "meshes":
                data = read_prepared_mesh(entry, directory, original)
            elif field == "materials":
                data = read_prepared_material(entry, original)
            else:
                data = read_prepared_texture(entry, directory, original)
            bundle[field][original] = data
    bundle["warnings"] = {item["sourceObjectPath"] for item in validate_seq(payload.get("warnings", []), "mesh import warnings")
                          if isinstance(item.get("sourceObjectPath"), str)}
    return bundle


def prepare_meshes(plans, mesh_map, mesh_imports):
    meshes, imports, effective_by_class = {}, {}, {}
    for blueprint in plans:
        components = blueprint.get("components", [])
        overrides = blueprint.get("componentOverrides", [])
        for component in [*components, *overrides]:
            original = component.get("staticMesh")
            if original is None or original in meshes or original in imports:
                continue
            if original not in mesh_map and original in mesh_imports:
                imports[original] = mesh_imports[original]
                continue
            target = mesh_map.get(original, original)
            mesh = unreal.load_asset(target)
            require(isinstance(mesh, unreal.StaticMesh) and mesh.get_path_name() == target,
                    f"Static mesh {original} resolves to {target}, which is not an existing StaticMesh asset.")
            meshes[original] = mesh

        effective = effective_by_class.get(blueprint["parentClass"], {}).copy()
        source_class = blueprint["sourceAsset"]
        for component in components:
            effective[source_class, component["name"]] = component
        for patch in overrides:
            key = (patch["ownerClass"], patch["name"])
            updated = effective[key].copy()
            if patch.get("clearStaticMesh"):
                updated.pop("staticMesh", None)
            if "staticMesh" in patch:
                updated["staticMesh"] = patch["staticMesh"]
            effective[key] = updated
        effective_by_class[source_class] = effective

        for (owner_class, name), component in effective.items():
            if "socket" not in component:
                continue
            parent = effective[owner_class, component["parent"]]
            reference = parent.get("staticMesh")
            socket = component["socket"]
            context = f"{blueprint['name']}.{name}"
            require(reference in meshes or reference in imports, f"Socket {socket} in {context} requires an assigned static mesh.")
            if reference in imports:
                staged_sockets = imports[reference]["sockets"]
                require(any(item["name"].casefold() == socket.casefold() for item in staged_sockets),
                        f"Prepared static mesh {reference} does not have socket {socket} in {context}.")
            else:
                mesh = meshes[reference]
                require(mesh.find_socket(socket) is not None,
                        f"Static mesh {mesh.get_path_name()} does not have socket {socket} in {context}.")
    return meshes, imports


def validate_interchange_content_path(content_directory):
    if os.name != "nt":
        return
    # Match UE 5.8 Interchange's minimum remaining Windows asset path budget.
    from ctypes import WinDLL, c_ubyte
    try:
        long_paths = WinDLL("ntdll.dll").RtlAreLongPathsEnabled
        long_paths.restype = c_ubyte
        uses_long_paths = bool(long_paths())
    except AttributeError:
        uses_long_paths = False
    limit = 32767 if uses_long_paths else 260
    length = len(content_directory.encode("utf-16-le")) // 2
    require(limit - length >= 60,
            "Unreal Interchange needs a shorter project path for automatic mesh import. "
            f"Content path: {content_directory}.")


def resolve_surface_asset(path, native_type, description, declared_warnings, warnings):
    asset = unreal.load_asset(path)
    if asset is None and path.startswith("/Game/") and path in declared_warnings:
        warnings.append({"sourceObjectPath": path, "message": f"Kept an unbound {description.lower()} reference."})
        return None
    require(isinstance(asset, native_type) and asset.get_path_name() == path,
            f"{description} {path} is not an existing asset of the required type.")
    return asset


def prepare_surfaces(meshes, bundle):
    materials, material_imports = {}, {}
    textures, texture_imports, warnings = {}, {}, []
    for mesh in meshes.values():
        for slot in mesh["materialSlots"]:
            path = slot.get("materialObjectPath")
            if not path or path in materials or path in material_imports:
                continue
            if path in bundle["materials"]:
                material_imports[path] = bundle["materials"][path]
            else:
                materials[path] = resolve_surface_asset(path, unreal.MaterialInterface, "Material", bundle["warnings"], warnings)
    for material in material_imports.values():
        for texture_field, _, _, _ in MATERIAL_CHANNELS:
            path = material.get(texture_field) if texture_field else None
            if not path or path in textures or path in texture_imports:
                continue
            if path in bundle["textures"]:
                texture_imports[path] = bundle["textures"][path]
            else:
                textures[path] = resolve_surface_asset(path, unreal.Texture2D, "Texture", bundle["warnings"], warnings)
    return materials, material_imports, textures, texture_imports, warnings


def import_prepared_asset(object_path, file, pipeline, native_type, description):
    package = object_path.rsplit(".", 1)[0]
    directory, name = package.rsplit("/", 1)
    manager = unreal.InterchangeManager.get_interchange_manager_scripted()
    source = manager.create_source_data(str(file))
    parameters = unreal.ImportAssetParameters(is_automated=True, replace_existing=False, destination_name=name,
                                               override_pipelines=[unreal.SoftObjectPath(pipeline.get_path_name())])
    imported = list(manager.import_asset(directory, source, parameters))
    require(len(imported) == 1 and isinstance(imported[0], native_type) and imported[0].get_path_name() == object_path,
            f"Could not import one {description} at the exact path {object_path}.")
    return imported[0]


def import_texture(path, data, pipeline):
    name = path.rsplit(".", 1)[1]
    pipeline.set_editor_property("asset_name", name)
    texture = import_prepared_asset(path, data["file"], pipeline, unreal.Texture2D, "Texture2D")
    require(texture.blueprint_get_size_x() == data["width"] and texture.blueprint_get_size_y() == data["height"],
            f"Imported texture dimensions do not match the prepared source at {path}.")
    compression = unreal.TextureCompressionSettings.TC_NORMALMAP if data["normalMap"] else unreal.TextureCompressionSettings.TC_DEFAULT
    texture.set_editor_property("compression_settings", compression)
    texture.set_editor_property("srgb", data["srgb"])
    return texture


def create_material(path, data, textures):
    package = path.rsplit(".", 1)[0]
    directory, name = package.rsplit("/", 1)
    material = unreal.AssetToolsHelpers.get_asset_tools().create_asset(
        name, directory, unreal.Material, unreal.MaterialFactoryNew())
    require(material is not None and material.get_path_name() == path, f"Could not create material {path}.")
    library = unreal.MaterialEditingLibrary
    applied = {}
    for index, (texture_field, scalar_field, property_name, output) in enumerate(MATERIAL_CHANNELS):
        texture_path = data.get(texture_field) if texture_field else None
        scalar = data.get(scalar_field) if scalar_field else None
        texture = textures.get(texture_path)
        if texture is not None:
            node = library.create_material_expression(material, unreal.MaterialExpressionTextureSample, -420, index * 230)
            require(node is not None, f"Could not create {texture_field} in {path}.")
            node.set_editor_property("texture", texture)
            if texture.get_editor_property("compression_settings") == unreal.TextureCompressionSettings.TC_NORMALMAP:
                sampler = unreal.MaterialSamplerType.SAMPLERTYPE_NORMAL
            elif texture.get_editor_property("srgb"):
                sampler = unreal.MaterialSamplerType.SAMPLERTYPE_COLOR
            else:
                sampler = unreal.MaterialSamplerType.SAMPLERTYPE_LINEAR_COLOR
            node.set_editor_property("sampler_type", sampler)
            applied[texture_field] = texture_path
        elif isinstance(scalar, dict):
            node = library.create_material_expression(material, unreal.MaterialExpressionConstant3Vector, -420, index * 230)
            require(node is not None, f"Could not create {scalar_field} in {path}.")
            node.set_editor_property("constant", unreal.LinearColor(**scalar))
            output = ""
            applied[scalar_field] = scalar
        elif scalar is not None:
            node = library.create_material_expression(material, unreal.MaterialExpressionConstant, -420, index * 230)
            require(node is not None, f"Could not create {scalar_field} in {path}.")
            node.set_editor_property("r", scalar)
            output = ""
            applied[scalar_field] = scalar
        else:
            continue
        material_property = getattr(unreal.MaterialProperty, property_name)
        require(library.connect_material_property(node, output, material_property),
                f"Could not wire {property_name} in {path}.")
    library.recompile_material(material)
    return material, applied


def apply_collision(mesh, entry):
    if not any(entry[key] for key in COLLISION_SHAPES):
        return
    boxes, spheres, capsules = [], [], []
    for item in entry["boxes"]:
        box = unreal.KBoxElem()
        box.set_editor_property("center", unreal.Vector(**item["center"]))
        box.set_editor_property("rotation", unreal.Rotator(**item["rotation"]))
        for axis in VECTOR_FIELDS:
            box.set_editor_property(axis, item["dimensions"][axis])
        boxes.append(box)
    for item in entry["spheres"]:
        sphere = unreal.KSphereElem()
        sphere.set_editor_property("center", unreal.Vector(**item["center"]))
        sphere.set_editor_property("radius", item["radius"])
        spheres.append(sphere)
    for item in entry["capsules"]:
        capsule = unreal.KSphylElem()
        capsule.set_editor_property("center", unreal.Vector(**item["center"]))
        capsule.set_editor_property("rotation", unreal.Rotator(**item["rotation"]))
        capsule.set_editor_property("radius", item["radius"])
        capsule.set_editor_property("length", item["length"])
        capsules.append(capsule)
    body = mesh.get_editor_property("body_setup")
    require(body is not None, f"The mesh has no BodySetup for its recovered collision: {mesh.get_path_name()}.")
    aggregate = unreal.KAggregateGeom()
    aggregate.set_editor_property("box_elems", boxes)
    aggregate.set_editor_property("sphere_elems", spheres)
    aggregate.set_editor_property("sphyl_elems", capsules)
    body.set_editor_property("agg_geom", aggregate)


def apply_material_slots(mesh, bindings, materials, mesh_editor):
    if not bindings:
        return
    native_slots = list(mesh.get_editor_property("static_materials"))
    target_names = {item["slotName"].casefold(): item["slotIndex"] for item in bindings}
    remaps = []
    for lod in range(mesh.get_num_lods()):
        for section in range(mesh.get_num_sections(lod)):
            old_index = mesh_editor.get_lod_material_slot(mesh, lod, section)
            require(0 <= old_index < len(native_slots), f"Invalid native section slot in {mesh.get_path_name()} LOD {lod}.")
            if len(bindings) == 1:
                target_index = 0
            else:
                native = native_slots[old_index]
                names = {str(native.get_editor_property(field)).casefold()
                         for field in ("material_slot_name", "imported_material_slot_name")}
                candidates = {target_names[name] for name in names if name in target_names}
                require(len(candidates) == 1,
                        f"Could not identify material slot for {mesh.get_path_name()} LOD {lod} section {section}.")
                target_index = candidates.pop()
            remaps.append((lod, section, old_index, target_index))

    assigned = [unreal.StaticMaterial(material_interface=materials.get(item.get("materialObjectPath")),
                                      material_slot_name=item["slotName"]) for item in bindings]
    mesh.set_editor_property("static_materials", [*assigned, *native_slots[len(assigned):]])
    for lod, section, old_index, target_index in remaps:
        if old_index != target_index:
            mesh_editor.set_lod_material_slot(mesh, target_index, lod, section)
    if len(native_slots) > len(assigned):
        mesh.set_editor_property("static_materials", assigned)
    for lod, section, _, target_index in remaps:
        require(mesh_editor.get_lod_material_slot(mesh, lod, section) == target_index,
                f"Could not preserve material slot for {mesh.get_path_name()} LOD {lod} section {section}.")


def import_static_mesh(object_path, entry, pipeline, materials, mesh_editor):
    mesh = import_prepared_asset(object_path, entry["geometry"], pipeline, unreal.StaticMesh, "StaticMesh")
    require(mesh.get_num_lods() == 1 and mesh.get_num_triangles(0) > 0,
            f"The imported StaticMesh must have one nonempty base LOD at {object_path}.")
    for index, geometry in entry["lods"]:
        result = mesh_editor.import_lod(mesh, index, str(geometry))
        require(result == index and mesh.get_num_lods() == index + 1 and mesh.get_num_triangles(index) > 0,
                f"Could not import nonempty LOD {index} at {object_path}.")
    apply_material_slots(mesh, entry["materialSlots"], materials, mesh_editor)
    apply_collision(mesh, entry)
    sockets = entry["sockets"]
    for index, item in enumerate(sockets):
        name = item["name"]
        require(mesh.find_socket(name) is None, f"The imported mesh already contains socket {name} in {object_path}.")
        socket = unreal.StaticMeshSocket(outer=mesh, name=f"RecoveredSocket_{index + 1}")
        socket.set_editor_property("socket_name", name)
        for key, property_name, _ in TRANSFORMS:
            if key in item:
                value_type = unreal.Rotator if key == "relativeRotation" else unreal.Vector
                socket_property = "relative_scale" if key == "relativeScale" else property_name
                socket.set_editor_property(socket_property, value_type(**item[key]))
        if item.get("tag") is not None:
            socket.set_editor_property("tag", item["tag"])
        mesh.add_socket(socket)
        require(mesh.find_socket(name) == socket, f"Could not author socket {name} in {object_path}.")
    return mesh


def component_class(class_path):
    return unreal.StaticMeshComponent if class_path == STATIC_MESH_COMPONENT_CLASS else unreal.SceneComponent


def apply_component_properties(template, component, meshes):
    for key, property_name, _ in TRANSFORMS:
        if key in component:
            value_type = unreal.Rotator if key == "relativeRotation" else unreal.Vector
            template.set_editor_property(property_name, value_type(**component[key]))
    for key, property_name in COMPONENT_FLAGS:
        if key in component:
            template.set_editor_property(property_name, component[key])
    if "staticMesh" in component or component.get("clearStaticMesh"):
        mesh = meshes.get(component.get("staticMesh"))
        template.set_static_mesh(mesh)
        require(template.get_editor_property("static_mesh") == mesh,
                f"Could not assign the static mesh to {component['name']}.")


def local_scs_nodes(blueprint):
    node_type = unreal.load_class(None, "/Script/Engine.SCS_Node")
    require(node_type is not None, "This editor does not provide SCS attachment nodes.")
    owner = blueprint.get_outer().get_path_name() + "."
    return {node.get_path_name(): node for node in unreal.ObjectIterator(node_type) if node.get_path_name().startswith(owner)}


def create_component_overrides(blueprint, patches, recovered, meshes):
    if not patches:
        return
    lib = unreal.BlueprintEditorLibrary
    data_api = unreal.SubobjectDataBlueprintFunctionLibrary
    subobjects = unreal.get_engine_subsystem(unreal.SubobjectDataSubsystem)
    require(subobjects is not None, "This editor does not provide the subobject data subsystem.")
    handles = subobjects.k2_gather_subobject_data_for_blueprint(blueprint)
    child_class = lib.generated_class(blueprint)
    for patch in patches:
        name = patch["name"]
        _, declaring_blueprint = recovered[patch["ownerClass"]]
        owner_class = lib.generated_class(declaring_blueprint)
        native_type = component_class(patch["classPath"])
        matches = {}
        for handle in handles:
            data = data_api.get_data(handle)
            if str(data_api.get_variable_name(data)) != name or not data_api.is_inherited_component(data):
                continue
            original = data_api.get_object(data)
            if isinstance(original, native_type) and original.get_class() == native_type.static_class() and original.get_outer() == owner_class:
                matches[original.get_path_name()] = data
        require(len(matches) == 1, f"Could not identify inherited component {name} on its restored declaring class.")
        override = data_api.get_object_for_blueprint(next(iter(matches.values())), blueprint)
        require(isinstance(override, native_type) and override.get_outer() == child_class,
                f"Could not create the child's override template for {name}.")
        apply_component_properties(override, patch, meshes)


def component_report(component, meshes):
    report = component.copy()
    if "staticMesh" in component:
        report["resolvedStaticMesh"] = meshes[component["staticMesh"]].get_path_name()
    return report


def create_components(blueprint, components, meshes):
    if not components:
        return
    data_api = unreal.SubobjectDataBlueprintFunctionLibrary
    subobjects = unreal.get_engine_subsystem(unreal.SubobjectDataSubsystem)
    require(subobjects is not None, "This editor does not provide the subobject data subsystem.")
    existing = subobjects.k2_gather_subobject_data_for_blueprint(blueprint)
    require(existing, "Could not find the new Blueprint actor's subobject data.")
    actor = existing[0]
    handles = {}
    for component in components:
        name = component["name"]
        attachment = component.get("parent")
        native_type = component_class(component["classPath"])
        socket = component.get("socket")
        before = local_scs_nodes(blueprint) if socket else {}
        if component.get("isDefaultSceneRoot", False):
            roots = [handle for handle in existing if data_api.is_default_scene_root(data_api.get_data(handle))]
            require(len(roots) == 1, "Could not resolve the Blueprint's synthesized scene root.")
            handle = roots[0]
        else:
            params = unreal.AddNewSubobjectParams(
                parent_handle=handles[attachment] if attachment else actor,
                new_class=native_type, blueprint_context=blueprint, conform_transform_to_parent=False)
            handle, error = subobjects.add_new_subobject(params)
            require(not str(error), f"Could not create scene component {name}: {error}.")
            require(subobjects.rename_subobject(handle, unreal.Text(name)), f"Could not name scene component {name}.")
            if attachment is None:
                require(subobjects.make_new_scene_root(actor, handle, blueprint),
                        f"Could not make {name} the root component.")
        data = data_api.get_data(handle)
        template = data_api.get_object_for_blueprint(data, blueprint)
        require(isinstance(template, native_type) and template.get_class() == native_type.static_class() and
                str(data_api.get_variable_name(data)) == name, f"Could not resolve the created component {name}.")
        if socket:
            added = [node for path, node in local_scs_nodes(blueprint).items() if path not in before]
            require(len(added) == 1, f"Could not identify the new socket attachment node for {name}.")
            # Author the native SCS attachment name before compilation and serialization.
            system = unreal.get_default_object(unreal.SystemLibrary.static_class())
            system.call_method("SetNamePropertyByName", args=(added[0], "AttachToName", socket))
        apply_component_properties(template, component, meshes)
        handles[name] = handle


def implemented_events(blueprint):
    return {str(item.name) for item in unreal.BlueprintEditorLibrary.list_events(blueprint) if item.is_implemented}


def create_event_node(blueprint, graph, event, types, used_names):
    lib = unreal.BlueprintEditorLibrary
    name = event["name"]
    inputs = event["inputs"]
    if not inputs:
        node = graph.add_custom_event_node(name)
        require(node is not None, f"Could not create custom event {name}.")
        return node, name, {}

    # The editor assigns a unique custom event name when copying a dispatcher signature.
    # A dispatcher named X naturally creates X_0. Other public names get a typed wrapper.
    preferred = name[:-2] if name.endswith("_0") else ""
    draft = preferred if IDENTIFIER.fullmatch(preferred) and preferred.lower() != "none" and preferred.casefold() not in used_names else ""
    if not draft or not lib.add_event_dispatcher(blueprint, draft):
        digest = sha256(name.encode("ascii")).hexdigest()[:16]
        draft = f"UEBulkExportEvent_{digest}"
        while draft.casefold() in used_names:
            draft += "_"
        require(lib.add_event_dispatcher(blueprint, draft), f"Could not stage the signature for {name}.")

    before = implemented_events(blueprint)
    try:
        for parameter in inputs:
            require(lib.add_event_dispatcher_parameter(blueprint, draft, parameter["name"], types[parameter["type"]]),
                    f"Could not add the typed signature to {name}.")
        node = graph.add_dispatcher_event_node(draft)
        require(node is not None, f"Could not create the typed custom event {name}.")
    finally:
        require(lib.remove_event_dispatcher(blueprint, draft), f"Could not remove the temporary signature for {name}.")

    added = implemented_events(blueprint) - before
    require(len(added) == 1, f"Could not identify the new custom event for {name}.")
    native_name = added.pop()
    parameters = {item["name"]: lib.find_output_pin(node, item["name"], types[item["type"]]) for item in inputs}
    require(all(pin.is_valid() for pin in parameters.values()), f"A typed parameter is missing from {name}.")
    return node, native_name, parameters


def create_event_wrapper(blueprint, event, native_name, types):
    lib = unreal.BlueprintEditorLibrary
    name = event["name"]
    target_class = lib.generated_class(blueprint)
    target = unreal.find_object(target_class, native_name) if target_class else None
    require(target is not None, f"Could not resolve the custom event implementation for {name}.")
    graph = unreal.BlueprintGraphEditor.create_and_edit_function_graph(blueprint, name)
    require(graph is not None and graph.get_graph().get_name() == name,
            f"Could not preserve the public event name {name}.")
    parameters = {item["name"]: graph.add_graph_input_parameter(item["name"], types[item["type"]])
                  for item in event["inputs"]}
    call = graph.add_call_function_node(target.get_path_name())
    require(call is not None, f"Could not create the event call for {name}.")
    connect(graph.find_graph_entry_pin(), lib.find_execute_pin(call), f"Could not execute the event call in {name}.")
    for parameter, pin in parameters.items():
        connect(pin, lib.find_input_pin(call, parameter), f"Could not wire the event parameter in {name}.")
    return graph


def create_blueprint(plan, full_path, recovered, meshes):
    lib = unreal.BlueprintEditorLibrary
    graph_api = unreal.BlueprintGraphEditor
    parent_name = plan["parentClass"]
    if parent_name == ACTOR_CLASS:
        parent = unreal.Actor.static_class()
    elif parent_name == LIBRARY_CLASS:
        parent = unreal.BlueprintFunctionLibrary.static_class()
    else:
        _, parent_blueprint = recovered[parent_name]
        parent = lib.generated_class(parent_blueprint)
    require(parent is not None, f"Could not resolve parent class {parent_name}.")
    blueprint = lib.create_blueprint_asset_with_parent(full_path, parent)
    require(blueprint is not None and lib.get_blueprint_parent_class(blueprint) == parent,
            f"Could not create a Blueprint with its matching parent at {full_path}.")
    types = {name: lib.get_basic_type_by_name(name) for name in ("int", "bool")}
    create_components(blueprint, plan.get("components", []), meshes)
    create_component_overrides(blueprint, plan.get("componentOverrides", []), recovered, meshes)

    graphs, events, wrappers = [], [], []
    if plan["variables"] or plan.get("events"):
        graph = graph_api.get_graph_editor_by_name(blueprint, "EventGraph")
        require(graph is not None, "The new Blueprint has no editable EventGraph.")
        for variable in plan["variables"]:
            value = pin_literal(variable["defaultValue"]) if "defaultValue" in variable else ""
            require(graph.add_member_variable(variable["name"], types[variable["type"]], value),
                    f"Could not create variable {variable['name']}.")

        graphs.append(("EventGraph", graph))
        used_names = {item["name"].casefold() for field in ("variables", "functions", "events", "components")
                      for item in plan.get(field, [])}
        for index, event in enumerate(plan.get("events", [])):
            name = event["name"]
            body = event["body"]
            node, native_name, parameters = create_event_node(blueprint, graph, event, types, used_names)
            used_names.add(native_name.casefold())
            row = index * 230
            lib.set_node_pos(node, unreal.IntPoint(760, row))
            status = {"name": name, "status": body["kind"]}
            if body["kind"] == "property-write":
                status.update(add_property_write(graph, lib.find_output_pin(node, "then"), parameters, body, name))
            else:
                comment = graph.add_comment_node("UNRECOVERED STUB\n" + body["reason"],
                                                 unreal.Vector2D(760, row - 100), unreal.Vector2D(540, 100))
                require(comment is not None, f"Could not mark the event stub in {name}.")
                status["reason"] = body["reason"]
            if native_name != name:
                wrappers.append((event, native_name))
                status.update({"entryPointKind": "function-wrapper", "customEvent": native_name})
            events.append(status)

    if wrappers:
        require(lib.compile_blueprint(blueprint), f"Could not compile the custom events in {full_path}.")
        for event, native_name in wrappers:
            graphs.append((event["name"], create_event_wrapper(blueprint, event, native_name, types)))

    functions = []
    for function in plan["functions"]:
        name = function["name"]
        override = function.get("overrideClass")
        if override:
            overridden_graph = lib.add_function_override(blueprint, name)
            graph = graph_api.get_graph_editor(overridden_graph) if overridden_graph else None
        else:
            graph = graph_api.create_and_edit_function_graph(blueprint, name)
        require(graph is not None and graph.get_graph().get_name() == name,
                f"Could not create graph with the exact name {name}.")
        graphs.append((name, graph))
        pure = function.get("pure", False)
        if pure and not override:
            graph.set_is_pure_function()
        parameters = {}
        for parameter in function["inputs"]:
            if parameter["type"] == "object":
                _, target_blueprint = recovered[parameter["typeReference"]]
                target_class = lib.generated_class(target_blueprint)
                require(target_class is not None, f"Could not resolve the recovered type of {name}.{parameter['name']}.")
                pin_type = lib.get_object_reference_type(target_class)
            else:
                pin_type = types[parameter["type"]]
            pin = graph.add_graph_input_parameter(parameter["name"], pin_type)
            require(pin.is_valid(), f"Could not create input {name}.{parameter['name']}.")
            parameters[parameter["name"]] = pin

        returned = None
        if override:
            nodes = graph.list_all_nodes()
            parents = [node for node in nodes if node.get_class().get_name() == "K2Node_CallParentFunction"]
            returns = [node for node in nodes if node.get_class().get_name() == "K2Node_FunctionResult"]
            require(len(parents) == 1 and len(returns) == 1, f"Could not identify the inherited override nodes in {name}.")
            graph.remove_nodes(parents)
            returned = returns[0]
        else:
            for parameter in function["outputs"]:
                returned = graph.add_graph_output_parameter(parameter["name"], types[parameter["type"]])
                require(returned is not None, f"Could not create output {name}.{parameter['name']}.")
        body = function["body"]
        kind = body["kind"]
        if returned is not None and kind != "conditional-return":
            connect(graph.find_graph_entry_pin(), lib.find_execute_pin(returned), f"Could not connect the result in {name}.")

        status = {"name": name, "status": kind}
        if override:
            _, ancestor = recovered[override]
            status["overrideClass"] = lib.generated_class(ancestor).get_path_name()
        if kind == "constant-return":
            pin = lib.find_input_pin(returned, "ReturnValue")
            require(pin.is_valid() and pin.set_pin_value(pin_literal(body["value"])),
                    f"Could not set the recovered constant in {name}.")
        elif kind == "parameter-return":
            connect(parameters[body["parameter"]], lib.find_input_pin(returned, "ReturnValue"),
                    f"Could not connect the recovered return parameter in {name}.")
            status["parameter"] = body["parameter"]
        elif kind == "conditional-return":
            branch = graph.add_branch_node()
            other = graph.add_return_node()
            require(branch is not None and other is not None, f"Could not create both branch paths in {name}.")
            entry = graph.find_graph_entry_pin()
            connect(entry, lib.find_execute_pin(branch), f"Could not start branch in {name}.")
            lib.set_node_pos(entry.get_owning_node(), unreal.IntPoint(0, 0))
            lib.set_node_pos(branch, unreal.IntPoint(320, 0))
            lib.set_node_pos(returned, unreal.IntPoint(650, -120))
            lib.set_node_pos(other, unreal.IntPoint(650, 180))
            connect(parameters[body["condition"]], lib.find_input_pin(branch, "Condition"),
                    f"Could not connect condition in {name}.")
            for label, result, key in (("then", returned, "whenTrue"), ("else", other, "whenFalse")):
                connect(lib.find_output_pin(branch, label), lib.find_execute_pin(result),
                        f"Could not connect {label} in {name}.")
                pin = lib.find_input_pin(result, "ReturnValue")
                require(pin.is_valid() and pin.set_pin_value(pin_literal(body[key])),
                        f"Could not set {label} return value in {name}.")
            status.update({"condition": body["condition"], "whenTrue": body["whenTrue"], "whenFalse": body["whenFalse"]})
        elif kind == "property-read":
            property_name = body["property"]
            getter = graph.add_get_member_variable_node(property_name)
            require(getter is not None, f"Could not create the property getter in {name}.")
            connect(lib.find_output_pin(getter, property_name), lib.find_input_pin(returned, "ReturnValue"),
                    f"Could not wire the property getter in {name}.")
            lib.set_node_pos(graph.find_graph_entry_pin().get_owning_node(), unreal.IntPoint(0, 0))
            lib.set_node_pos(getter, unreal.IntPoint(320, 130))
            lib.set_node_pos(returned, unreal.IntPoint(650, 0))
            status["property"] = property_name
        elif kind == "property-write":
            status.update(add_property_write(graph, graph.find_graph_entry_pin(), parameters, body, name))
        elif kind in ("function-call-return", "instance-call-return"):
            target_plan, target_blueprint = recovered[body["targetClass"]]
            target_class = lib.generated_class(target_blueprint)
            target_name = body["targetFunction"]
            target = unreal.find_object(target_class, target_name) if target_class else None
            require(target is not None and target.get_outer() == target_class,
                    f"Could not resolve the recovered function {target_name} in {target_plan['name']}.")
            call = graph.add_call_function_node(target.get_path_name())
            require(call is not None, f"Could not add the call to {target.get_path_name()}.")
            if kind == "instance-call-return":
                receiver = body["receiver"]
                connect(parameters[receiver], lib.find_input_pin(call, "self"),
                        f"Could not connect the recovered call receiver in {name}.")
                status["receiver"] = receiver
            else:
                callee = next(item for item in target_plan["functions"] if item["name"] == target_name)
                argument = body["arguments"][0]["parameter"]
                connect(parameters[argument], lib.find_input_pin(call, callee["inputs"][0]["name"]),
                        f"Could not connect the recovered call argument in {name}.")
            connect(lib.find_output_pin(call, "ReturnValue"), lib.find_input_pin(returned, "ReturnValue"),
                    f"Could not connect the call result in {name}.")
            lib.set_node_pos(graph.find_graph_entry_pin().get_owning_node(), unreal.IntPoint(0, 0))
            lib.set_node_pos(call, unreal.IntPoint(320, 130))
            lib.set_node_pos(returned, unreal.IntPoint(650, 0))
            status.update({"targetClass": target_class.get_path_name(), "targetFunction": target_name})
        else:
            comment = graph.add_comment_node("UNRECOVERED STUB\n" + body["reason"],
                                             unreal.Vector2D(0, -190), unreal.Vector2D(540, 165))
            require(comment is not None, f"Could not mark the stub in {name}.")
            status["reason"] = body["reason"]
        functions.append(status)

    compiled = lib.compile_blueprint(blueprint)
    for name, graph in graphs:
        require(not graph.list_nodes_with_errors(), f"Blueprint compiler reported errors in {name}.")
    require(compiled, f"Blueprint compilation failed for {full_path}.")
    inherited_defaults = plan.get("inheritedDefaults", [])
    if inherited_defaults:
        default = unreal.get_default_object(lib.generated_class(blueprint))
        for field in inherited_defaults:
            default.set_editor_property(field["name"], field["value"])
            require(default.get_editor_property(field["name"]) == field["value"],
                    f"Could not set the inherited default for {field['name']}.")

    report = {
        "schemaVersion": 1,
        "sourceAsset": plan["sourceAsset"],
        "createdBlueprint": full_path,
        "variables": [{"name": item["name"], "type": item["type"], "defaultRecovered": "defaultValue" in item}
                      for item in plan["variables"]],
        "functions": functions,
        "omissions": plan.get("omissions", []),
    }
    if parent_name not in (ACTOR_CLASS, LIBRARY_CLASS):
        report["parentClass"] = parent.get_path_name()
    if events:
        report["events"] = events
    if plan.get("components"):
        report["components"] = [component_report(entry, meshes) for entry in plan["components"]]
    if plan.get("componentOverrides"):
        report["componentOverrides"] = [component_report(entry, meshes) for entry in plan["componentOverrides"]]
    if inherited_defaults:
        report["inheritedDefaults"] = inherited_defaults
    return blueprint, report


@contextmanager
def project_lock():
    saved = filesystem_path(unreal.Paths.convert_relative_path_to_full(unreal.Paths.project_saved_dir()))
    lock = saved / "UEBulkExport" / "blueprint-import.lock"
    lock.parent.mkdir(parents=True, exist_ok=True)
    with lock.open("a+b") as stream:
        try:
            if os.name == "nt":
                import msvcrt
                stream.seek(0)
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as error:
            raise RuntimeError("Another Blueprint recovery is using the target project.") from error
        yield


def persist(assets, created, packages, report, report_path):
    report_created = False
    try:
        transaction = report_path.open("x", encoding="utf-8") if report_path else nullcontext()
        with transaction as stream:
            report_created = stream is not None
            require(assets.save_loaded_assets(created, only_if_is_dirty=False), "Could not save the recovered assets.")
            require(all(path.is_file() for path in packages), "A recovered asset was not saved.")
            if stream:
                json.dump(report, stream, ensure_ascii=False, indent=2)
                stream.write("\n")
    except Exception as error:
        if any(path.exists() for path in packages):
            try:
                assets.delete_loaded_assets(created)
            except Exception as cleanup_error:
                unreal.log_warning(f"Could not roll back Blueprint recovery: {cleanup_error}")
        remaining = [str(path) for path in packages if path.exists()]
        if report_created:
            try:
                report_path.unlink()
            except OSError:
                remaining.append(str(report_path))
        if remaining:
            raise RuntimeError("Could not fully roll back Blueprint recovery. Files remaining: " + ", ".join(remaining)) from error
        raise


def recover(plan, destination, report_path, mesh_map, bundle):
    plans, paths = validate_plan(plan, destination)
    assets = unreal.get_editor_subsystem(unreal.EditorAssetSubsystem)
    content_directory = unreal.Paths.convert_relative_path_to_full(unreal.Paths.project_content_dir())
    content_root = filesystem_path(content_directory)
    with project_lock():
        meshes, mesh_imports = prepare_meshes(plans, mesh_map, bundle["meshes"])
        materials, material_imports, textures, texture_imports, warnings = prepare_surfaces(mesh_imports, bundle)
        prepared = [*mesh_imports, *material_imports, *texture_imports]
        output_paths = [*paths, *(source.rsplit(".", 1)[0] for source in prepared)]
        packages = [content_root.joinpath(*path.split("/")[2:]).with_suffix(".uasset") for path in output_paths]
        names = set()
        for path, package in zip(output_paths, packages):
            add_unique(names, path, "destination asset")
            require(not assets.does_asset_exist(path) and not package.exists() and
                    not package.with_suffix(".umap").exists(), f"An asset already exists at {path}.")

        created = []
        mesh_reports, material_reports, texture_reports = [], [], []
        if mesh_imports or texture_imports:
            validate_interchange_content_path(content_directory)
        if texture_imports:
            pipeline = unreal.InterchangeGenericTexturePipeline()
            for path, data in texture_imports.items():
                texture = import_texture(path, data, pipeline)
                textures[path] = texture
                created.append(texture)
                texture_reports.append({
                    "sourceObjectPath": path, "createdTexture": texture.get_path_name(),
                    **{field: data[field] for field in ("width", "height", "srgb", "normalMap")},
                })
        for path, data in material_imports.items():
            material, properties = create_material(path, data, textures)
            materials[path] = material
            created.append(material)
            material_reports.append({"sourceObjectPath": path, "createdMaterial": material.get_path_name(),
                                     "properties": properties})
        if mesh_imports:
            pipeline = unreal.InterchangeGenericAssetsPipeline()
            pipeline.get_editor_property("mesh_pipeline").set_editor_property("collision", False)
            material_pipeline = pipeline.get_editor_property("material_pipeline")
            material_pipeline.set_editor_property("import_materials", False)
            material_pipeline.get_editor_property("texture_pipeline").set_editor_property("import_textures", False)
            mesh_editor = unreal.get_default_object(unreal.StaticMeshEditorSubsystem.static_class())
            for source, entry in mesh_imports.items():
                mesh = import_static_mesh(source, entry, pipeline, materials, mesh_editor)
                meshes[source] = mesh
                created.append(mesh)
                mesh_reports.append({
                    "sourceObjectPath": source,
                    "createdStaticMesh": mesh.get_path_name(),
                    "sockets": [item["name"] for item in entry["sockets"]],
                    "lods": [{"lodIndex": index, "triangles": mesh.get_num_triangles(index)}
                             for index in range(mesh.get_num_lods())],
                    **{field: entry[field] for field in (*COLLISION_SHAPES, "materialSlots") if entry[field]},
                })

        recovered, reports = {}, []
        for blueprint, path in zip(plans, paths):
            asset, result = create_blueprint(blueprint, path, recovered, meshes)
            recovered[blueprint["sourceAsset"]] = (blueprint, asset)
            created.append(asset)
            reports.append(result)
        for path, package in zip(output_paths, packages):
            require(not package.exists() and not package.with_suffix(".umap").exists(),
                    f"An asset appeared during recovery at {path}.")
        report = reports[0] if plan["schemaVersion"] == 1 else {"schemaVersion": 2, "createdBlueprints": reports}
        if mesh_reports:
            report["importedMeshes"] = mesh_reports
        if material_reports:
            report["importedMaterials"] = material_reports
        if texture_reports:
            report["importedTextures"] = texture_reports
        if warnings:
            report["meshWarnings"] = warnings
        persist(assets, created, packages, report, report_path)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", default=os.getenv("UEBULKEXPORT_BLUEPRINT_PLAN"))
    parser.add_argument("--destination", default=os.getenv("UEBULKEXPORT_BLUEPRINT_DESTINATION",
                                                             "/Game/RecoveredBlueprints"))
    parser.add_argument("--report", default=os.getenv("UEBULKEXPORT_BLUEPRINT_REPORT"))
    parser.add_argument("--mesh-map", default=os.getenv("UEBULKEXPORT_BLUEPRINT_MESH_MAP"))
    args = parser.parse_args()
    require(args.plan, "Set --plan or UEBULKEXPORT_BLUEPRINT_PLAN.")
    with filesystem_path(args.plan).open("r", encoding="utf-8-sig") as stream:
        plan = json.load(stream)
    report_path = filesystem_path(args.report) if args.report else None
    if report_path:
        require(report_path.parent.is_dir(), f"Report folder does not exist: {report_path.parent}.")
        require(not report_path.exists(), f"Report already exists: {report_path}.")
    if not hasattr(unreal, "BlueprintGraphEditor"):
        raise RuntimeError("This adapter requires the Blueprint Graph Editor Python API in Unreal Engine 5.8.")
    imports = read_mesh_imports(os.getenv("UEBULKEXPORT_BLUEPRINT_MESH_IMPORTS"))
    report = recover(plan, args.destination, report_path, read_mesh_map(args.mesh_map), imports)
    unreal.log(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
