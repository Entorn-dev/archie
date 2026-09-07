# Third-party notices

Archie includes third-party components under their own licenses. Those licenses apply only to the respective components and do not change Archie's Apache-2.0 license.

The Linux release archive includes the exact license and notice files supplied by its bundled npm and NuGet packages under `licenses/npm` and `licenses/nuget`. The release also includes these reviewed notices:

- `ModelContextProtocol.Core` 2.2.0 — Apache-2.0 with earlier code retained under MIT during the project's licensing transition. See `licenses/ModelContextProtocol.Core-2.2.0.LICENSE.txt`.
- `elkjs` 0.11.0 — Eclipse Public License 2.0. See `licenses/npm/elkjs-0.11.0-LICENSE.md`. The corresponding source is available from <https://github.com/kieler/elkjs> under EPL-2.0.

The dependency inventory is defined by the committed lockfiles. Release packaging fails if a bundled npm or NuGet package has neither license metadata nor a supplied license file, so changes to dependencies cannot silently omit their licensing information.
