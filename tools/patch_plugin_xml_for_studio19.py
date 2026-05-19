"""Patch plugin.xml in the Studio 19 build output to reference Studio 19's Sdl.* assemblies.

The canonical source plugin.xml in src/Supervertaler.Trados/ contains
``Sdl.*, Version=18.0.0.0`` extension-attribute assembly references (Trados Studio 2024).
Studio 2026 ships the same Sdl.* assemblies at ``Version=19.0.0.0`` (same PublicKeyToken,
verified by inspecting Studio19Beta/Sdl.TranslationResourcesApi.dll), so the only thing the
Studio 19 build needs is a textual ``18.0.0.0`` → ``19.0.0.0`` swap on the Sdl.* refs.

Invoked from the .csproj as a post-build step when TradosStudioVersion=19. Operates on
the file in the build output directory, never on the source-tree copy.

Usage:
    python patch_plugin_xml_for_studio19.py <path/to/output/Supervertaler.Trados.plugin.xml>
"""
import re
import sys


def patch(path: str) -> None:
    with open(path, "rb") as f:
        raw = f.read()

    if raw[:2] == b"\xff\xfe":
        text = raw[2:].decode("utf-16-le")
        encoding = "utf-16-le"
        write_bom = True
    elif raw[:2] == b"\xfe\xff":
        text = raw[2:].decode("utf-16-be")
        encoding = "utf-16-be"
        write_bom = True
    else:
        text = raw.decode("utf-8")
        encoding = "utf-8"
        write_bom = False

    # Match only Sdl.* assembly references to avoid touching unrelated 18.0.0.0 strings
    # (the plugin's own version uses a different format and lives in different attrs).
    pattern = re.compile(r"(Sdl\.[A-Za-z0-9_.]+, Version=)18\.0\.0\.0(,)")
    new_text, count = pattern.subn(r"\g<1>19.0.0.0\g<2>", text)

    if count == 0:
        print(f"  [patch_plugin_xml] WARNING: no Sdl.*, Version=18.0.0.0 refs found in {path}")
        return

    out = new_text.encode(encoding)
    if write_bom:
        bom = b"\xff\xfe" if encoding == "utf-16-le" else b"\xfe\xff"
        out = bom + out

    with open(path, "wb") as f:
        f.write(out)

    print(f"  [patch_plugin_xml] Rewrote {count} Sdl.* assembly refs 18.0.0.0 -> 19.0.0.0")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(1)
    patch(sys.argv[1])
