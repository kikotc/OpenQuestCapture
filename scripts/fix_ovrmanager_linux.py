#!/usr/bin/env python3
"""
Reapplies the Linux compile fix to OVRManagerEditor.cs in Library/PackageCache.

The Meta XR SDK has a bug where `OVRManager manager` is declared inside a
Windows/Mac #if block but used outside it, causing a compile error on Linux.
This script moves the declaration before the #if block.

Must be run from the OpenQuestCapture project root.
Run after any fresh Unity package import that wipes Library/PackageCache.
"""
import sys
import glob
import os

pattern = "Library/PackageCache/com.meta.xr.sdk.core@*/Scripts/Editor/OVRManagerEditor.cs"
matches = glob.glob(pattern)
if not matches:
    print("ERROR: OVRManagerEditor.cs not found. Is Library/PackageCache present?")
    sys.exit(1)

path = matches[0]
content = open(path).read()

broken = (
    "#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_ANDROID\n"
    "        OVRManager manager = target as OVRManager;\n"
    "#endif"
)
fixed = (
    "        OVRManager manager = target as OVRManager;\n"
    "#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_ANDROID\n"
    "#endif"
)

if broken not in content:
    if "        OVRManager manager = target as OVRManager;" in content:
        print("Already patched or already correct — no change needed.")
    else:
        print("ERROR: Expected pattern not found. SDK version may have changed.")
    sys.exit(0)

content = content.replace(broken, fixed)
open(path, "w").write(content)
print(f"Patched: {path}")
