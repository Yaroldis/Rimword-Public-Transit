#!/usr/bin/env python3
"""Static checks without RimWorld, Unity or the .NET SDK."""
from pathlib import Path
import sys
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def require(condition, message):
    if not condition:
        raise ValueError(message)


def main():
    xml_files = sorted(ROOT.rglob("*.xml"))
    # Ignore generated binaries/obj files when validating locally.
    xml_files = [p for p in xml_files if not any(x in ("bin", "obj") for x in p.parts)]
    require(xml_files, "No XML files were found")
    for file in xml_files:
        ET.parse(file)

    about = ET.parse(ROOT / "About" / "About.xml").getroot()
    require(about.tag == "ModMetaData", "About.xml: expected ModMetaData root")
    require(about.findtext("packageId") == "RimTransit.PublicTransit", "Unexpected packageId")
    versions = [n.text for n in about.findall("supportedVersions/li")]
    require(versions == ["1.6"], f"Target version must be 1.6 only: {versions}")
    deps = {n.text.lower() for n in about.findall("modDependencies/li/packageId") if n.text}
    require({"brrainz.harmony", "smashphil.vehicleframework"} <= deps, "Missing required dependency")

    defs = ET.parse(ROOT / "Defs" / "ThingDefs" / "TransitStops.xml").getroot()
    stop = next((n for n in defs.findall("ThingDef") if n.findtext("defName") == "PT_TransitStop"), None)
    require(stop is not None, "PT_TransitStop ThingDef not found")
    require(stop.findtext("thingClass") == "PublicTransit.Buildings.Building_TransitStop", "Invalid stop class name")
    require(stop.findtext("rotatable") == "true", "Transit stop must be rotatable")
    require(stop.findtext("defaultPlacingRot") == "South", "Default docking side must be South")
    base_texture = ROOT / "Textures" / (stop.findtext("graphicData/texPath") + ".png")
    require(base_texture.exists(), f"Stop UI texture missing: {base_texture}")
    require(base_texture.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"), "Stop UI texture is not PNG")
    require(stop.findtext("graphicData/graphicClass") == "Graphic_Single", "Stop must use native Q/E rotation")
    require(stop.findtext("graphicData/drawRotated") == "true", "Stop graphic must turn when planning")
    require(stop.findtext("passability") == "Impassable", "Stop post must not be drivable")
    require(stop.findtext("placeWorkers/li") ==
        "PublicTransit.Buildings.PlaceWorker_TransitStopDocking", "Missing ghost docking marker")
    require((ROOT / "Source" / "PublicTransit" / "Buildings" /
        "PlaceWorker_TransitStopDocking.cs").exists(), "Missing PlaceWorker class")

    project = ET.parse(ROOT / "Source" / "PublicTransit" / "PublicTransit.csproj").getroot()
    require(project.findtext("PropertyGroup/TargetFramework") == "net472", "Wrong target framework")
    references = {n.attrib.get("Include"): n.attrib.get("Version") for n in project.findall("ItemGroup/PackageReference")}
    require(references.get("Krafs.Rimworld.Ref") == "1.6.4871", "Wrong reference package")
    # Prevent missing C# class files in Git patches (regression from 0.3.1).
    for relative in (
        "Source/PublicTransit/PublicTransitSettings.cs",
        "Source/PublicTransit/Core/TransitMapComponent.cs",
        "Source/PublicTransit/Core/TransitVehicleController.cs",
        "Source/PublicTransit/Core/VehicleAdapter.cs",
        "Source/PublicTransit/Routes/TransitRunState.cs",
        "Source/PublicTransit/Jobs/JobDriver_BoardTransitDriver.cs",
    ):
        require((ROOT / relative).is_file(), f"Required C# file missing: {relative}")
    job_defs = ET.parse(ROOT / "Defs" / "JobDefs" / "TransitDriverJobs.xml").getroot()
    job = next((n for n in job_defs.findall("JobDef")
        if n.findtext("defName") == "PT_BoardTransitDriver"), None)
    require(job is not None and job.findtext("driverClass") ==
        "PublicTransit.Jobs.JobDriver_BoardTransitDriver", "Invalid boarding JobDef")
    for language in ("Russian", "English"):
        injected = ET.parse(ROOT / "Languages" / language / "DefInjected" / "JobDef" / "TransitDriverJobs.xml").getroot()
        require(injected.findtext("PT_BoardTransitDriver.reportString"),
            f"{language}: boarding job translation missing")
    # Verify every keyed translation used in C# exists in both languages.
    keyed = {}
    for language in ("Russian", "English"):
        root = ET.parse(ROOT / "Languages" / language / "Keyed" / "PublicTransit.xml").getroot()
        keys = [node.tag for node in root]
        require(len(keys) == len(set(keys)), f"{language}: duplicate translation key")
        keyed[language] = {node.tag: node.text or "" for node in root}
    require(set(keyed["Russian"]) == set(keyed["English"]), "English/Russian translation keys differ")
    used_keys = set()
    for source in (ROOT / "Source" / "PublicTransit").rglob("*.cs"):
        used_keys.update(re.findall(r'"(PT_[a-zA-Z0-9]+)"\.Translate\(', source.read_text(encoding="utf-8")))
    missing = used_keys - set(keyed["Russian"])
    require(not missing, f"C# references missing translation keys: {sorted(missing)}")
    for key in used_keys:
        ru_args = set(re.findall(r"\{[0-9]+\}", keyed["Russian"][key]))
        en_args = set(re.findall(r"\{[0-9]+\}", keyed["English"][key]))
        require(ru_args == en_args, f"{key}: placeholder mismatch between translations")

    print(f"OK: {len(xml_files)} XML files, {len(used_keys)} localized strings, RimWorld 1.6, VF/Harmony, texture, C# config")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, ET.ParseError, OSError) as exc:
        print(f"VALIDATION FAILED: {exc}", file=sys.stderr)
        sys.exit(1)
