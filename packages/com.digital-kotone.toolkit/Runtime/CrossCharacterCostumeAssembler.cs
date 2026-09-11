using System;
using System.Collections.Generic;
using System.Linq;
using ActorAnimation;
using Unity.Collections;
using UnityEngine;

namespace GakumasPhotoMode
{
    /// <summary>
    /// Retargets a complete donor body mesh onto a recipient body rig.
    ///
    /// Gakumas normally ships one body prefab per character/costume pair.  A
    /// cross-character wardrobe experiment therefore has to do more than replace
    /// the prefab: the recipient's humanoid skeleton remains canonical, donor
    /// garment branches are transplanted below matching anatomy bones, and every
    /// donor SMR is rebound to the assembled hierarchy by bone name.
    /// </summary>
    public static class CrossCharacterCostumeAssembler
    {
        public sealed class Result
        {
            public readonly HashSet<Transform> activeBones = new HashSet<Transform>();
            public int donorRendererCount;
            public int reboundBoneCount;
            public int transplantedBranchCount;
            public int clonedChainCount;
            public int clonedStaticColliderCount;
            public int removedRecipientBranchCount;
            public int readableSourceRendererCount;
            public int rescaledMeshVertexCount;
            public int postSkinScaleRendererCount;
            public float recipientOverDonorScale = 1f;
        }

        public static Result Assemble(
            GameObject recipientBody,
            GameObject donorBody,
            string donorBundleName = null)
        {
            if (recipientBody == null) throw new ArgumentNullException("recipientBody");
            if (donorBody == null) throw new ArgumentNullException("donorBody");

            Result result = new Result();
            SkinnedMeshRenderer[] recipientRenderers =
                recipientBody.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            SkinnedMeshRenderer[] donorRenderers =
                donorBody.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int donorGarmentCollisionMask = CollectGarmentCollisionMask(donorBody);
            result.recipientOverDonorScale = ResolveAuthoredScale(recipientBody.transform) /
                Mathf.Max(0.0001f, ResolveAuthoredScale(donorBody.transform));

            // Remove the recipient's costume-only transform branches before the
            // donor arrives.  Leaving them hidden is insufficient: duplicate
            // Sleeve/Skirt/Ribbon bone names make AvatarBuilder and name binding
            // nondeterministic, and their ActorSwing components would keep running.
            HashSet<Transform> recipientCostumeRoots = FindCostumeBranchRoots(
                recipientBody.transform,
                recipientRenderers.SelectMany(value => value.bones ?? new Transform[0]));
            foreach (SkinnedMeshRenderer renderer in recipientRenderers)
            {
                if (renderer != null) UnityEngine.Object.DestroyImmediate(renderer.gameObject);
            }
            foreach (Transform root in TopLevelOnly(recipientCostumeRoots))
            {
                if (root == null) continue;
                UnityEngine.Object.DestroyImmediate(root.gameObject);
                result.removedRecipientBranchCount++;
            }
            foreach (ActorSwingChain chain in recipientBody.GetComponentsInChildren<ActorSwingChain>(true))
            {
                if (chain != null) UnityEngine.Object.DestroyImmediate(chain);
            }

            Dictionary<string, Transform> recipientByName = BuildCanonicalLookup(recipientBody.transform);
            IEnumerable<Transform> donorRelevantBones = donorRenderers
                .SelectMany(value => value.bones ?? new Transform[0])
                .Where(value => value != null)
                .Concat(donorBody.GetComponentsInChildren<ActorSwingDynamicBone>(true)
                    .Select(value => value.transform))
                .Concat(donorBody.GetComponentsInChildren<ActorAnimationQuartzDriverSkirtBone>(true)
                    .Select(value => value.transform));
            HashSet<Transform> donorCostumeRoots = FindCostumeBranchRoots(
                donorBody.transform, donorRelevantBones);

            Dictionary<Transform, Transform> boneMap = new Dictionary<Transform, Transform>();
            foreach (Transform donorTransform in donorBody.GetComponentsInChildren<Transform>(true))
            {
                Transform recipientTransform;
                if (IsCanonicalBone(donorTransform.name) &&
                    recipientByName.TryGetValue(donorTransform.name, out recipientTransform))
                {
                    boneMap[donorTransform] = recipientTransform;
                }
            }

            // Move complete donor-only branches.  Components and serialized
            // references remain intact because the original Transform objects move
            // with the branch rather than being reconstructed approximately.
            foreach (Transform branchRoot in TopLevelOnly(donorCostumeRoots))
            {
                if (branchRoot == null || branchRoot.parent == null) continue;
                Transform recipientParent = FindMappedAncestor(branchRoot.parent, boneMap);
                if (recipientParent == null)
                {
                    Debug.LogWarning("[Wardrobe] No recipient parent for donor branch " + branchRoot.name);
                    continue;
                }
                branchRoot.SetParent(recipientParent, false);
                foreach (Transform moved in branchRoot.GetComponentsInChildren<Transform>(true))
                {
                    boneMap[moved] = moved;
                    // Renderer bones omit unweighted ActorSwing `*_End`
                    // transforms.  Those endpoints are nevertheless live: the
                    // child entry owns the final rendered parent's parameters
                    // and original ActorSwingChain layers reference them
                    // directly.  A moved donor costume branch is already the
                    // exact ownership boundary, so retain its full transform
                    // graph in the runtime mask rather than truncating it to
                    // SMR-weighted bones.
                    result.activeBones.Add(moved);
                }
                result.transplantedBranchCount++;
            }

            // Any non-costume helper that shares a stable canonical name can still
            // be mapped after branch transplantation.  This covers twist/finger
            // bones while never aliasing authored garment branches by accident.
            foreach (Transform donorTransform in donorBody.GetComponentsInChildren<Transform>(true))
            {
                if (boneMap.ContainsKey(donorTransform)) continue;
                Transform recipientTransform;
                if (IsCanonicalBone(donorTransform.name) &&
                    recipientByName.TryGetValue(donorTransform.name, out recipientTransform))
                    boneMap[donorTransform] = recipientTransform;
            }

            CloneDonorChains(
                donorBody, recipientBody, boneMap,
                result.recipientOverDonorScale, result);
            CloneMissingDonorGarmentColliders(
                donorBody, recipientBody, boneMap,
                donorGarmentCollisionMask,
                result.recipientOverDonorScale, result);

            foreach (SkinnedMeshRenderer renderer in donorRenderers)
            {
                if (renderer == null) continue;
                if (UseReadableSourceMesh(
                        renderer, donorBody.transform, donorBundleName))
                    result.readableSourceRendererCount++;
                Transform[] sourceBones = renderer.bones ?? new Transform[0];
                int rescaledVertices = CompensateBindLocalScale(
                    renderer, result.recipientOverDonorScale);
                result.rescaledMeshVertexCount += rescaledVertices;
                if (rescaledVertices == 0 && ApplyPostSkinScale(
                        renderer, result.recipientOverDonorScale))
                    result.postSkinScaleRendererCount++;
                Transform[] rebound = new Transform[sourceBones.Length];
                for (int index = 0; index < sourceBones.Length; index++)
                {
                    Transform source = sourceBones[index];
                    Transform target;
                    if (source != null && boneMap.TryGetValue(source, out target))
                    {
                        rebound[index] = target;
                        AddWithAncestors(result.activeBones, target, recipientBody.transform);
                        result.reboundBoneCount++;
                    }
                    else
                    {
                        rebound[index] = source;
                        Debug.LogWarning(string.Format(
                            "[Wardrobe] Unmapped donor SMR bone {0} on {1}",
                            source == null ? "<null>" : source.name, renderer.name));
                    }
                }
                renderer.bones = rebound;
                Transform mappedRoot;
                if (renderer.rootBone != null && boneMap.TryGetValue(renderer.rootBone, out mappedRoot))
                    renderer.rootBone = mappedRoot;
                Transform donorParent = renderer.transform.parent;
                Transform targetParent;
                if (donorParent == donorBody.transform)
                {
                    targetParent = recipientBody.transform;
                }
                else if (!boneMap.TryGetValue(donorParent, out targetParent))
                {
                    targetParent = recipientBody.transform;
                }
                renderer.transform.SetParent(targetParent, false);
                renderer.updateWhenOffscreen = true;
                renderer.gameObject.name += "__retargeted";
                result.donorRendererCount++;
            }

            UnityEngine.Object.DestroyImmediate(donorBody);
            Debug.Log(string.Format(
                "[Wardrobe] Recipient-rig retarget ready: renderers={0} reboundBones={1} " +
                "donorBranches={2} removedRecipientBranches={3} chains={4} staticColliders={5} " +
                "activeBones={6} recipientOverDonorScale={7:0.000000} readableSources={8} " +
                "rescaledVertices={9} postSkinScaleRenderers={10}",
                result.donorRendererCount, result.reboundBoneCount,
                result.transplantedBranchCount, result.removedRecipientBranchCount,
                result.clonedChainCount, result.clonedStaticColliderCount,
                result.activeBones.Count,
                result.recipientOverDonorScale, result.readableSourceRendererCount,
                result.rescaledMeshVertexCount,
                result.postSkinScaleRendererCount));
            return result;
        }

        private static bool UseReadableSourceMesh(
            SkinnedMeshRenderer renderer,
            Transform donorRoot,
            string donorBundleName)
        {
            if (renderer == null || renderer.sharedMesh == null ||
                donorRoot == null || string.IsNullOrEmpty(donorBundleName))
                return false;
            string rendererPath = RelativePath(renderer.transform, donorRoot);
            string key = SanitizeResourcePart(donorBundleName) + "__" +
                SanitizeResourcePart(rendererPath) + "__" +
                SanitizeResourcePart(renderer.sharedMesh.name);
            Mesh readable = Resources.Load<Mesh>("ReadableBodyMeshes/" + key);
            if (readable == null)
            {
                Debug.LogWarning("[Wardrobe] No readable donor mesh resource: " + key);
                return false;
            }
            if (!readable.isReadable)
            {
                Debug.LogWarning("[Wardrobe] Donor mesh resource was stripped: " + key);
                return false;
            }
            renderer.sharedMesh = readable;
            Debug.Log(string.Format(
                "[Wardrobe] Readable donor mesh selected: {0} vertices={1}",
                key, readable.vertexCount));
            return true;
        }

        private static string RelativePath(Transform value, Transform root)
        {
            if (value == null || value == root) return "root";
            List<string> parts = new List<string>();
            Transform cursor = value;
            while (cursor != null && cursor != root)
            {
                parts.Add(cursor.name);
                cursor = cursor.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static string SanitizeResourcePart(string value)
        {
            if (string.IsNullOrEmpty(value)) return "root";
            char[] result = value.ToCharArray();
            for (int index = 0; index < result.Length; index++)
            {
                char character = result[index];
                if (character == '/' || character == '\\' || character == ':' ||
                    character == '*' || character == '?' || character == '"' ||
                    character == '<' || character == '>' || character == '|')
                    result[index] = '_';
            }
            return new string(result);
        }

        private static int CollectGarmentCollisionMask(GameObject donorBody)
        {
            int mask = 0;
            foreach (ActorSwingDynamicBone dynamicBone in
                     donorBody.GetComponentsInChildren<ActorSwingDynamicBone>(true))
            {
                if (dynamicBone == null || dynamicBone.dynamicCollider == null ||
                    !IsGarmentDynamicBone(dynamicBone.transform.name))
                    continue;
                mask |= dynamicBone.dynamicCollider.collisionMask;
            }
            return mask;
        }

        private static bool IsGarmentDynamicBone(string boneName)
        {
            if (string.IsNullOrEmpty(boneName) ||
                boneName.IndexOf("Skirt", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            string part = boneName;
            if (part.StartsWith("Left", StringComparison.Ordinal)) part = part.Substring(4);
            else if (part.StartsWith("Right", StringComparison.Ordinal)) part = part.Substring(5);
            return !part.StartsWith("UpLegSkin", StringComparison.Ordinal) &&
                   !part.StartsWith("LegSkin", StringComparison.Ordinal) &&
                   !part.StartsWith("HipSkin", StringComparison.Ordinal);
        }

        /// <summary>
        /// ActorSwing static colliders are stored on ordinary anatomy bones, not
        /// on the costume-only branches which are moved above.  A synthetic
        /// cross-character swap therefore has to carry across any collision mask
        /// required by the donor garment but absent from the recipient rig.  The
        /// fktn cstm-0000 jacket, for example, uses mask 64 on its panel chains;
        /// hmsz cstm-0000 has no mask-64 body colliders at all, so merely moving
        /// the dynamic branches leaves the coat collisionless and lets it open to
        /// roughly ninety degrees.
        /// </summary>
        private static void CloneMissingDonorGarmentColliders(
            GameObject donorBody,
            GameObject recipientBody,
            IDictionary<Transform, Transform> boneMap,
            int donorGarmentCollisionMask,
            float authoredScale,
            Result result)
        {
            int recipientMask = 0;
            foreach (ActorSwingStaticBone value in
                     recipientBody.GetComponentsInChildren<ActorSwingStaticBone>(true))
            {
                if (value != null && value.staticCollider != null)
                    recipientMask |= value.staticCollider.collisionMask;
            }
            int missingMask = donorGarmentCollisionMask & ~recipientMask;
            if (missingMask == 0) return;

            foreach (ActorSwingStaticBone source in
                     donorBody.GetComponentsInChildren<ActorSwingStaticBone>(true))
            {
                if (source == null || source.staticCollider == null ||
                    source.staticCollider.type == 4)
                    continue;
                int requiredMask = source.staticCollider.collisionMask & missingMask;
                if (requiredMask == 0) continue;
                Transform target;
                if (!boneMap.TryGetValue(source.transform, out target) || target == null)
                    continue;

                ActorSwingStaticBone clone =
                    target.gameObject.AddComponent<ActorSwingStaticBone>();
                clone.enabled = false;
                clone.staticCollider = CloneCollider(
                    source.staticCollider, requiredMask, authoredScale);
                result.clonedStaticColliderCount++;
                Debug.Log(string.Format(
                    "[Wardrobe] Donor garment collider {0}->{1}: type={2} mask={3} " +
                    "scale={4:0.000000}",
                    source.transform.name, target.name,
                    clone.staticCollider.type, clone.staticCollider.collisionMask,
                    authoredScale));
            }
        }

        private static SwingCollider CloneCollider(
            SwingCollider source,
            int collisionMask,
            float authoredScale)
        {
            float scale = Mathf.Max(0.0001f, authoredScale);
            return new SwingCollider
            {
                type = source.type,
                collisionMask = collisionMask,
                vector3_A = source.vector3_A * scale,
                vector3_B = source.vector3_B * scale,
                float_A = source.float_A * scale,
                float_B = source.float_B * scale,
                seatDynamicCorrectionDisableCollisionMask =
                    source.seatDynamicCorrectionDisableCollisionMask,
            };
        }

        private static void CloneDonorChains(
            GameObject donorBody,
            GameObject recipientBody,
            IDictionary<Transform, Transform> boneMap,
            float authoredScale,
            Result result)
        {
            foreach (ActorSwingChain source in donorBody.GetComponentsInChildren<ActorSwingChain>(true))
            {
                if (source == null || IsUnderMovedBranch(source.transform, boneMap)) continue;
                Transform targetTransform;
                if (!boneMap.TryGetValue(source.transform, out targetTransform)) continue;
                ActorSwingChain target = targetTransform.gameObject.AddComponent<ActorSwingChain>();
                target.enabled = false;
                target.rootBones = MapDynamicBones(source.rootBones, boneMap);
                if (source.chains != null && source.chains.layers != null)
                {
                    target.chains = new SwingChainLayers
                    {
                        layers = source.chains.layers.Select(layer => layer == null
                            ? null
                            : new SwingChainLayer
                            {
                                active = layer.active,
                                around = layer.around,
                                radius = layer.radius * authoredScale,
                                smoothing = layer.smoothing,
                                bones = MapDynamicBones(layer.bones, boneMap),
                            }).ToArray(),
                    };
                }
                result.clonedChainCount++;
            }
        }

        /// <summary>
        /// Same-motif character bundles scale both the skeleton offsets and the
        /// vertex offsets around each bind bone.  Rebinding only the bones changes
        /// their origins but leaves the latter at the donor size, which is why the
        /// first wardrobe pass visibly swapped leg thickness.  Scale each vertex
        /// around its weighted bind-pose centre before the renderer is rebound.
        /// For a uniformly authored character scale this reconstructs the target
        /// mesh exactly for one-bone vertices and remains continuous at blends.
        /// </summary>
        private static int CompensateBindLocalScale(
            SkinnedMeshRenderer renderer,
            float authoredScale)
        {
            if (renderer == null || renderer.sharedMesh == null ||
                Mathf.Abs(authoredScale - 1f) < 0.00001f)
                return 0;

            Mesh source = renderer.sharedMesh;
            try
            {
                Vector3[] vertices = source.vertices;
                BoneWeight[] weights = source.boneWeights;
                Matrix4x4[] bindposes = source.bindposes;
                if (vertices == null || vertices.Length == 0 ||
                    bindposes == null || bindposes.Length == 0)
                {
                    Debug.LogWarning("[Wardrobe] Mesh cannot supply readable bind data: " + source.name);
                    return 0;
                }

                Vector3[] centres = new Vector3[bindposes.Length];
                for (int index = 0; index < bindposes.Length; index++)
                    centres[index] = bindposes[index].inverse.MultiplyPoint3x4(Vector3.zero);

                if (weights != null && weights.Length == vertices.Length)
                {
                    for (int index = 0; index < vertices.Length; index++)
                    {
                        BoneWeight weight = weights[index];
                        Vector3 centre = Vector3.zero;
                        float total = 0f;
                        AddWeightedCentre(ref centre, ref total, centres, weight.boneIndex0, weight.weight0);
                        AddWeightedCentre(ref centre, ref total, centres, weight.boneIndex1, weight.weight1);
                        AddWeightedCentre(ref centre, ref total, centres, weight.boneIndex2, weight.weight2);
                        AddWeightedCentre(ref centre, ref total, centres, weight.boneIndex3, weight.weight3);
                        ApplyScaleAroundCentre(vertices, index, centre, total, authoredScale);
                    }
                }
                else
                {
                    // Current Gakumas bundles use Unity's variable-influence
                    // stream. Mesh.boneWeights is consequently empty even though
                    // GetAllBoneWeights exposes the serialized skin records.
                    NativeArray<byte> counts = source.GetBonesPerVertex();
                    NativeArray<BoneWeight1> allWeights = source.GetAllBoneWeights();
                    try
                    {
                        if (counts.Length != vertices.Length || allWeights.Length == 0)
                        {
                            Debug.LogWarning(string.Format(
                                "[Wardrobe] Mesh skin stream mismatch on {0}: vertices={1} legacy={2} counts={3} weights={4}",
                                source.name, vertices.Length,
                                weights == null ? 0 : weights.Length,
                                counts.Length, allWeights.Length));
                            return 0;
                        }
                        int cursor = 0;
                        for (int index = 0; index < vertices.Length; index++)
                        {
                            Vector3 centre = Vector3.zero;
                            float total = 0f;
                            int influenceCount = counts[index];
                            for (int influence = 0; influence < influenceCount; influence++)
                            {
                                BoneWeight1 weight = allWeights[cursor++];
                                AddWeightedCentre(
                                    ref centre, ref total, centres,
                                    weight.boneIndex, weight.weight);
                            }
                            ApplyScaleAroundCentre(vertices, index, centre, total, authoredScale);
                        }
                    }
                    finally
                    {
                        if (counts.IsCreated) counts.Dispose();
                        if (allWeights.IsCreated) allWeights.Dispose();
                    }
                }

                Mesh corrected = UnityEngine.Object.Instantiate(source);
                corrected.name = source.name + "__recipient-scale";
                corrected.vertices = vertices;
                corrected.RecalculateBounds();
                renderer.sharedMesh = corrected;
                return vertices.Length;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(string.Format(
                    "[Wardrobe] Bind-local body-scale compensation failed for {0}: {1}",
                    source.name, exception.Message));
                return 0;
            }
        }

        private static void ApplyScaleAroundCentre(
            IList<Vector3> vertices,
            int index,
            Vector3 centre,
            float total,
            float authoredScale)
        {
            if (total > 0.0001f) centre /= total;
            Vector3 vertex = vertices[index];
            vertices[index] = centre + (vertex - centre) * authoredScale;
        }

        private static bool ApplyPostSkinScale(
            SkinnedMeshRenderer renderer,
            float authoredScale)
        {
            if (renderer == null || Mathf.Abs(authoredScale - 1f) < 0.00001f) return false;
            MaterialPropertyBlock properties = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(properties);
            // The CPU copy of production body meshes is intentionally stripped.
            // Scale the already-skinned horizontal offsets in every reconstructed
            // Actor pass instead; vertical proportions continue to come from the
            // recipient skeleton. This corrects the conspicuous donor leg/body
            // width without stretching the recipient's authored height twice.
            properties.SetVector("_WardrobeScaleCorrection",
                new Vector4(authoredScale, 1f, authoredScale, 0f));
            renderer.SetPropertyBlock(properties);
            Bounds bounds = renderer.localBounds;
            bounds.extents = Vector3.Scale(
                bounds.extents,
                new Vector3(authoredScale, 1f, authoredScale));
            renderer.localBounds = bounds;
            return true;
        }

        private static void AddWeightedCentre(
            ref Vector3 sum,
            ref float total,
            IList<Vector3> centres,
            int boneIndex,
            float weight)
        {
            if (weight <= 0f || boneIndex < 0 || boneIndex >= centres.Count) return;
            sum += centres[boneIndex] * weight;
            total += weight;
        }

        private static float ResolveAuthoredScale(Transform bodyRoot)
        {
            Transform marker = FindNamedTransform(bodyRoot, "BodyScaleRatio_DIS");
            if (marker != null && Mathf.Abs(marker.localPosition.y) > 0.0001f)
                return Mathf.Abs(marker.localPosition.y);
            Transform hips = FindNamedTransform(bodyRoot, "Hips");
            if (hips != null && Mathf.Abs(hips.localPosition.y) > 0.0001f)
                return Mathf.Abs(hips.localPosition.y);
            return 1f;
        }

        private static Transform FindNamedTransform(Transform root, string name)
        {
            if (root == null) return null;
            foreach (Transform value in root.GetComponentsInChildren<Transform>(true))
                if (string.Equals(value.name, name, StringComparison.Ordinal)) return value;
            return null;
        }

        private static ActorSwingDynamicBone[] MapDynamicBones(
            ActorSwingDynamicBone[] source,
            IDictionary<Transform, Transform> boneMap)
        {
            if (source == null) return null;
            ActorSwingDynamicBone[] result = new ActorSwingDynamicBone[source.Length];
            for (int index = 0; index < source.Length; index++)
            {
                if (source[index] == null) continue;
                Transform target;
                if (!boneMap.TryGetValue(source[index].transform, out target)) continue;
                result[index] = target.GetComponent<ActorSwingDynamicBone>();
            }
            return result;
        }

        private static bool IsUnderMovedBranch(
            Transform value,
            IDictionary<Transform, Transform> boneMap)
        {
            Transform mapped;
            return boneMap.TryGetValue(value, out mapped) && mapped == value;
        }

        private static Dictionary<string, Transform> BuildCanonicalLookup(Transform root)
        {
            Dictionary<string, Transform> result =
                new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform value in root.GetComponentsInChildren<Transform>(true))
            {
                if (!IsCanonicalBone(value.name) || result.ContainsKey(value.name)) continue;
                result.Add(value.name, value);
            }
            return result;
        }

        private static HashSet<Transform> FindCostumeBranchRoots(
            Transform bodyRoot,
            IEnumerable<Transform> relevantBones)
        {
            HashSet<Transform> result = new HashSet<Transform>();
            foreach (Transform bone in relevantBones.Distinct())
            {
                if (bone == null || IsCanonicalBone(bone.name)) continue;
                Transform current = bone;
                while (current.parent != null && current.parent != bodyRoot &&
                       !IsCanonicalBone(current.parent.name))
                    current = current.parent;
                if (current != bodyRoot && current.parent != null &&
                    IsCanonicalBone(current.parent.name))
                    result.Add(current);
            }
            return result;
        }

        private static IEnumerable<Transform> TopLevelOnly(HashSet<Transform> values)
        {
            return values.Where(value => value != null &&
                !values.Any(other => other != null && other != value && value.IsChildOf(other)))
                .OrderBy(value => HierarchyDepth(value))
                .ToArray();
        }

        private static Transform FindMappedAncestor(
            Transform value,
            IDictionary<Transform, Transform> boneMap)
        {
            while (value != null)
            {
                Transform mapped;
                if (boneMap.TryGetValue(value, out mapped)) return mapped;
                value = value.parent;
            }
            return null;
        }

        private static void AddWithAncestors(
            HashSet<Transform> values,
            Transform value,
            Transform stop)
        {
            while (value != null)
            {
                values.Add(value);
                if (value == stop) break;
                value = value.parent;
            }
        }

        private static int HierarchyDepth(Transform value)
        {
            int depth = 0;
            while (value != null && value.parent != null)
            {
                depth++;
                value = value.parent;
            }
            return depth;
        }

        private static bool IsCanonicalBone(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            switch (name)
            {
                case "Reference":
                case "Root":
                case "Root_M":
                case "Hips":
                case "Pelvis":
                case "Spine":
                case "Spine1":
                case "Spine2":
                case "Neck":
                case "Head":
                    return true;
            }
            if (name.IndexOf("Bust", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            string side = name.StartsWith("Left", StringComparison.Ordinal) ? "Left" :
                          name.StartsWith("Right", StringComparison.Ordinal) ? "Right" : null;
            if (side == null) return false;
            string part = name.Substring(side.Length);
            if (part == "Shoulder" || part == "Arm" || part == "ForeArm" ||
                part == "Hand" || part == "UpLeg" || part == "Leg" ||
                part == "Foot" || part == "ToeBase")
                return true;
            // These are character anatomy/deformation helpers, not costume
            // branches. The first swap treated the LegSkin chains as garment
            // bones, deleted the recipient copies and transplanted donor-length
            // chains. That put the calf weights on the wrong longitudinal pivots
            // and produced the visible bowed/bulging lower leg. Across cstm-0000
            // and the shared othr-0002 motif these transforms are invariant for a
            // given character (within 2.1e-6 m), while their offsets carry the
            // fktn/hmsz 1.022701 character scale. Keep the recipient versions.
            if (part.StartsWith("UpLegSkin", StringComparison.Ordinal) ||
                part.StartsWith("LegSkin", StringComparison.Ordinal) ||
                part.StartsWith("HipSkin", StringComparison.Ordinal) ||
                part.EndsWith("_H", StringComparison.Ordinal) ||
                part == "Thigh_O" || part == "Waist_O")
                return true;
            if (part.StartsWith("HandThumb", StringComparison.Ordinal) ||
                part.StartsWith("HandIndex", StringComparison.Ordinal) ||
                part.StartsWith("HandMiddle", StringComparison.Ordinal) ||
                part.StartsWith("HandRing", StringComparison.Ordinal) ||
                part.StartsWith("HandPinky", StringComparison.Ordinal))
                return true;
            return part.StartsWith("ArmTwist", StringComparison.Ordinal) ||
                   part.StartsWith("ForeArmTwist", StringComparison.Ordinal) ||
                   part.StartsWith("UpLegTwist", StringComparison.Ordinal) ||
                   part.StartsWith("LegTwist", StringComparison.Ordinal);
        }
    }
}
