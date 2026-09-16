"""Source boundary guards only. Runtime image and real-character evidence are separate."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / 'packages/com.digital-kotone.toolkit/Runtime'


class ActorShadowContractTests(unittest.TestCase):
    def test_producer_is_explicit_and_does_not_modify_host_globals(self):
        source = (RUNTIME / 'SrpActorShadow.cs').read_text(encoding='utf-8')
        for forbidden in ('Camera.Render(', '.Submit(', 'Shader.SetGlobal', 'FindObjectsOfType', 'renderPipelineAsset='):
            self.assertNotIn(forbidden, source)
        for required in ('atlas.PrepareDirectional(direction, settings', 'context.ExecuteCommandBuffer(commands)',
                         'value <= sequence', 'Actor shadow output feedback', 'states[i].IsCurrent'):
            self.assertIn(required, source)

    def test_current_shadow_ticket_reaches_full_actor_and_sequence_guard(self):
        draw = (RUNTIME / 'ActorForwardDrawSet.cs').read_text(encoding='utf-8')
        compose = (RUNTIME / 'SrpActorForward.cs').read_text(encoding='utf-8')
        self.assertIn('public SrpActorShadow.Frame? selfShadow', draw)
        self.assertIn('!selfShadow.Value.IsCurrent', draw)
        self.assertIn('settings.selfShadow.Value.Bind(material)', draw)
        self.assertIn('draws.selfShadow.Value.sequence!=value', compose)

    def test_receiver_uses_linear_full_precision_depth_without_extra_sampler(self):
        source = (RUNTIME / 'Resources/ActorForwardShadow.cginc').read_text(encoding='utf-8')
        self.assertIn('UNITY_DECLARE_TEX2D_NOSAMPLER_FLOAT(_ActorForwardShadowMap)', source)
        self.assertNotIn('sampler2D', source)
        self.assertIn('(axial - depth.z) / depth.y', source)
        self.assertIn('UNITY_UV_STARTS_AT_TOP', source)
        self.assertIn('world + normal * depth.w', source)
        self.assertNotIn('_WorldSpaceCameraPos', source)
        self.assertNotIn('_CapturedLightDirection', source)
        self.assertIn('cross(ddx(world), ddy(world))', source)
        self.assertIn('dot((pixel + .5) / size - uv, slope)', source)
        fixture = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.ActorShadow.cs').read_text(encoding='utf-8')
        self.assertIn('constant-bias-acne-negative-control', fixture)
        self.assertIn('receiver-plane-removes-self-acne', fixture)

    def test_generic_caster_default_and_actor_coverage_are_separate(self):
        atlas = (RUNTIME / 'SceneLightShadowAtlas.cs').read_text(encoding='utf-8')
        shader = (RUNTIME / 'Resources/ActorLightShadowCaster.shader').read_text(encoding='utf-8')
        self.assertIn('caster.actorCoverage == null', atlas)
        self.assertIn('ActorLightShadowCaster', atlas)
        self.assertIn('if (_ShadowActorCoverage.y <= 0) discard', shader)
        self.assertIn('fwidth(alpha)', shader)
        self.assertIn('input.vertex.xyz * _ShadowVertexScale', shader)
        self.assertIn('SCENE_SHADOW_POINT', shader)
        self.assertNotIn('_HairFadeParameters', shader)

    def test_capture_preserves_submesh_property_block_precedence(self):
        source = (RUNTIME / 'ActorShadowInputs.cs').read_text(encoding='utf-8')
        for required in ('submesh.isEmpty ? common : submesh', 'ShadowCastingMode.Off',
                         'ShadowCastingMode.TwoSided', '_ActorTextureFrame', '_BaseMap_ST',
                         '_WardrobeScaleCorrection', 'Reserved', 'SceneLightShadowAtlas.ValidateCaster'):
            self.assertIn(required, source)
        self.assertNotIn('SetPropertyBlock', source)
        self.assertNotIn('sharedMaterial =', source)

    def test_new_asset_guids_are_valid_and_distinct(self):
        names = [RUNTIME / p for p in ('ActorShadowInputs.cs', 'SrpActorShadow.cs',
                 'Resources/ActorForwardShadow.cginc', 'Resources/ActorLightShadowCaster.shader')]
        names.append(ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.ActorShadow.cs')
        guids = []
        for path in names:
            text = Path(str(path) + '.meta').read_text(encoding='utf-8')
            match = re.search(r'^guid: ([a-f0-9]{32})$', text, re.M)
            self.assertIsNotNone(match, str(path))
            guids.append(match.group(1))
        self.assertEqual(len(guids), len(set(guids)))

    def test_full_suite_appends_shadow_without_reordering_prior_fixtures(self):
        source = (ROOT / 'unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs').read_text(encoding='utf-8')
        self.assertGreater(source.rindex('var fixture=VerifyActorShadow(report)'), source.rindex('var fixture=VerifySrpActor(report)'))


if __name__ == '__main__':
    unittest.main()
