using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed class PhotoActorRenderControls : ActorRenderControls
    {
        private Vector2 _panelScroll;

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8)) showPanel = !showPanel;
        }

        private void OnGUI()
        {
            if (!showPanel) return;
            GUILayout.BeginArea(new Rect(Screen.width - 310, 20, 290, Mathf.Min(590, Mathf.Max(160, Screen.height - 40))), GUI.skin.box);
            _panelScroll = GUILayout.BeginScrollView(_panelScroll);
            GUILayout.Label("ACTOR RENDERING / F8");
            outlines = GUILayout.Toggle(outlines, "Smooth-normal outline");
            hairCover = GUILayout.Toggle(hairCover, "Hair over eyes (stencil only)");
            GUI.enabled = LayerMaterialCount > 0;
            overrideLayer = GUILayout.Toggle(overrideLayer, "Override material Layer");
            GUI.enabled = LayerMaterialCount > 0 && overrideLayer;
            layerWeight = Slider("Sweat / messy layer", layerWeight, 0f, 1f);
            GUI.enabled = true;
            GUILayout.Label("Layer-enabled materials: " + LayerMaterialCount);
            overrideLighting = GUILayout.Toggle(overrideLighting, "Override story lighting");
            GUI.enabled = overrideLighting;
            worldSpaceLight = GUILayout.Toggle(worldSpaceLight, "World-space main light");
            lightAngle.x = Slider("Light X", lightAngle.x, -180f, 180f);
            lightAngle.y = Slider("Light Y", lightAngle.y, -90f, 90f);
            diffuseOffset = Slider("Diffuse offset", diffuseOffset, -1f, 1f);
            shadeStrength = Slider("Shade strength", shadeStrength, 0f, 1f);
            smoothnessScale = Slider("Smoothness", smoothnessScale, 0f, 2f);
            giScale = Slider("Ambient", giScale, 0f, 2f);
            additionalLightScale = Slider("Additional lights", additionalLightScale, 0f, 3f);
            GUI.enabled = true;
            overrideRim = GUILayout.Toggle(overrideRim, "Override view-space rim only");
            GUI.enabled = overrideRim;
            rimAngle.x = Slider("Rim yaw", rimAngle.x, -180f, 180f);
            rimAngle.y = Slider("Rim pitch", rimAngle.y, -90f, 90f);
            rimPower = Slider("Rim power (narrowness)", rimPower, 0.01f, 128f);
            rimBaseColorRatio = Slider("Rim surface tint", rimBaseColorRatio, 0f, 1f);
            rimIntensity = Slider("Rim intensity", rimIntensity, 0f, 4f);
            rimColor.r = Slider("Rim red", rimColor.r, 0f, 2f);
            rimColor.g = Slider("Rim green", rimColor.g, 0f, 2f);
            rimColor.b = Slider("Rim blue", rimColor.b, 0f, 2f);
            GUI.enabled = true;
            if (GUILayout.Button("Toggle two test lights")) ToggleTestLights();
            GUILayout.Label(string.Format("Outline {0} / Hair {1} / Lights {2}", OutlineDrawCount, HairCoverDrawCount, AdditionalLightCount));
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static float Slider(string label, float value, float min, float max)
        {
            GUILayout.Label(label + ": " + value.ToString("0.00"));
            return GUILayout.HorizontalSlider(value, min, max);
        }

    }
}
