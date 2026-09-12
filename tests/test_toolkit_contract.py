"""Package/application dependency contracts; runtime parity needs the Player probes."""
import json
import hashlib
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
PACKAGE = ROOT / 'packages/com.digital-kotone.toolkit'
APP = ROOT / 'unity/Assets/Applications/PhotoStudio'


class ToolkitBoundaryTests(unittest.TestCase):
    def test_package_has_declared_dependencies_and_importable_sample(self):
        package = json.loads((PACKAGE / 'package.json').read_text(encoding='utf-8'))
        self.assertEqual(package['name'], 'com.digital-kotone.toolkit')
        self.assertEqual(package['dependencies']['com.unity.mathematics'], '1.3.2')
        for sample in package['samples']:
            self.assertTrue((PACKAGE / sample['path'] / 'MinimalCharacterHost.cs').is_file())
        project = json.loads((ROOT / 'unity/Packages/manifest.json').read_text(encoding='utf-8'))
        dependency = project['dependencies'][package['name']]
        self.assertEqual((ROOT / 'unity/Packages' / dependency.removeprefix('file:')).resolve(), PACKAGE.resolve())

    def test_assembly_dependencies_point_from_application_to_core(self):
        core = json.loads((PACKAGE / 'Runtime/Gakumas.Toolkit.asmdef').read_text(encoding='utf-8'))
        app = json.loads((APP / 'Gakumas.PhotoStudio.asmdef').read_text(encoding='utf-8'))
        self.assertIn(core['name'], app['references'])
        self.assertNotIn(app['name'], core['references'])
        for path in (PACKAGE / 'Runtime').rglob('*.cs'):
            with self.subTest(file=path.name):
                source = path.read_text(encoding='utf-8')
                self.assertNotRegex(source, r'\b(?:PhotoModeApp|PhotoActorRenderControls|ActorRenderingSelfTest|ActorRenderingValidation|RenderDocCaptureBridge)\b')
        story = (PACKAGE / 'Runtime/StoryTimelinePlayer.cs').read_text(encoding='utf-8')
        self.assertIn('Initialize(CharacterSceneRuntime app, string stagingRoot)', story)

    def test_application_keeps_policies_and_core_has_explicit_start(self):
        core = (PACKAGE / 'Runtime/CharacterSceneRuntime.cs').read_text(encoding='utf-8')
        api = (PACKAGE / 'Runtime/CharacterSceneRuntime.Api.cs').read_text(encoding='utf-8')
        app = (APP / 'PhotoModeApp.cs').read_text(encoding='utf-8')
        self.assertIn('class PhotoModeApp : CharacterSceneRuntime', app)
        self.assertNotRegex(core, r'\b(?:Awake|OnGUI)\s*\(|Input\.|Screen\.SetResolution|QualitySettings\.|Application\.targetFrameRate')
        for contract in ('protected override void ConfigureApplication()',
                         'protected override void ProcessApplicationInput()',
                         'RuntimeArguments = Environment.GetCommandLineArgs();',
                         'ConfigureRendererFromArguments(commandLine);',
                         'AddComponent<PhotoActorRenderControls>()'):
            self.assertIn(contract, app)
        self.assertLess(app.index('ConfigureRendererFromArguments(commandLine);'),
                        app.index('Initialize(BundleCatalog.DefaultStagingRoot)'))
        for contract in ('Initialize(CharacterSceneOptions options)', 'Directory.Exists(options.DataRoot)',
                         'InitializeRuntime(Path.GetFullPath(options.DataRoot))',
                         '_orbit.enabled = options.EnableOrbitInput', 'StoryTimelinePlayer Timeline'):
            self.assertIn(contract, api)
        self.assertNotIn('Environment.GetCommandLineArgs()', api)

    def test_f8_panel_is_only_in_application(self):
        core = (PACKAGE / 'Runtime/ActorRenderControls.cs').read_text(encoding='utf-8')
        panel = (APP / 'PhotoActorRenderControls.cs').read_text(encoding='utf-8')
        self.assertNotRegex(core, r'\bOnGUI\s*\(|\bInput\.')
        self.assertIn('class PhotoActorRenderControls : ActorRenderControls', panel)
        self.assertIn('Input.GetKeyDown(KeyCode.F8)', panel)
        self.assertIn('ACTOR RENDERING / F8', panel)

    def test_native_render_state_is_created_after_monobehaviour_construction(self):
        core = (PACKAGE / 'Runtime/ActorRenderControls.cs').read_text(encoding='utf-8')
        self.assertIn('private MaterialPropertyBlock _ambientPacked;', core)
        enable = core.split('protected void OnEnable()', 1)[1].split('protected void OnPreCull()', 1)[0]
        self.assertIn('if (_ambientPacked == null) _ambientPacked = new MaterialPropertyBlock();', enable)

    def test_compatibility_assembly_names_are_preserved(self):
        definitions = sorted((PACKAGE / 'Runtime/Compatibility').rglob('*.asmdef'))
        self.assertEqual(sorted(json.loads(p.read_text(encoding='utf-8'))['name'] for p in definitions),
                         ['ActorAnimation.Runtime', 'campus-submodule.Runtime', 'vl-unity.Runtime'])

    def test_scene_still_uses_photo_app_script_guid(self):
        meta = (APP / 'PhotoModeApp.cs.meta').read_text(encoding='utf-8')
        scene = (ROOT / 'unity/Assets/Scenes/PhotoMode.unity').read_text(encoding='utf-8')
        # Tuanjie may serialize an encrypted GUID in meta, while the scene uses
        # its decoded ID. Preserve both frozen files, do not guess a decoder.
        baseline = json.loads((ROOT / 'config/source-baseline.json').read_text(encoding='utf-8'))['files']
        self.assertEqual(hashlib.sha256(meta.encode()).hexdigest(), baseline['unity/Assets/Scripts/PhotoModeApp.cs.meta'])
        self.assertEqual(hashlib.sha256(scene.encode()).hexdigest(), baseline['unity/Assets/Scenes/PhotoMode.unity'])

    def test_package_public_allowlist_contains_only_source_and_documentation(self):
        policy = json.loads((ROOT / 'public-files.json').read_text(encoding='utf-8'))
        names = [p for p in policy['files'] if p.startswith('packages/com.digital-kotone.toolkit/')]
        self.assertGreater(len(names), 100)
        for name in names:
            with self.subTest(file=name):
                self.assertIn(Path(name).suffix, {'.cs', '.meta', '.asmdef', '.shader', '.compute', '.cginc', '.hlsl', '.json', '.md'})
                self.assertNotIn('PrivateResources', name)
