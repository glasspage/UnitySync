using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Glasspage.UnitySync
{
    [InitializeOnLoad]
    internal static class UnitySyncSelectionPresence
    {
        private sealed class RemoteSelection
        {
            internal Guid PlayerId;
            internal Color Color;
            internal string[] ObjectIds;
        }

        private sealed class RingBatch
        {
            internal Guid PlayerId;
            internal Color Color;
            internal int RingIndex;
            internal readonly List<Renderer> Renderers = new List<Renderer>();
            internal readonly HashSet<int> RendererIds = new HashSet<int>();
        }

        private const float BaseOutlinePixels = 2f;
        private const float StackedOutlinePixels = 1f;
        private const int MaskSupersample = 2;
        private const int SupersamplePixelLimit = 2560 * 1440;

        private static readonly Dictionary<Guid, RemoteSelection> RemoteSelections =
            new Dictionary<Guid, RemoteSelection>();
        private static readonly MethodInfo NativeDrawOutlineMethod = FindNativeDrawOutlineMethod();

        private static Material _maskMaterial;
        private static Material _dilateMaterial;
        private static Material _compositeMaterial;
        private static RenderTexture _maskHighResolution;
        private static RenderTexture _mask;
        private static RenderTexture _outerHorizontal;
        private static RenderTexture _outer;
        private static RenderTexture _innerHorizontal;
        private static RenderTexture _inner;
        private static int _textureWidth;
        private static int _textureHeight;
        private static int _supersample = 1;

        static UnitySyncSelectionPresence()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseResources;
            EditorApplication.quitting += ReleaseResources;
            UnitySyncVisualSettings.Changed += SceneView.RepaintAll;
        }

        internal static void Apply(UnitySyncSelectionState selection, Guid localPlayerId)
        {
            if (selection.PlayerId == Guid.Empty || selection.PlayerId == localPlayerId)
            {
                return;
            }

            string[] objectIds = selection.ObjectIds ?? new string[0];
            string[] copiedIds = new string[objectIds.Length];
            Array.Copy(objectIds, copiedIds, objectIds.Length);
            RemoteSelections[selection.PlayerId] = new RemoteSelection
            {
                PlayerId = selection.PlayerId,
                Color = selection.Color,
                ObjectIds = copiedIds
            };
        }

        internal static void Remove(Guid playerId)
        {
            RemoteSelections.Remove(playerId);
        }

        internal static void Clear()
        {
            RemoteSelections.Clear();
            ReleaseRenderTextures();
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!UnitySyncVisualSettings.SelectionOutlines ||
                RemoteSelections.Count == 0 ||
                Event.current == null ||
                Event.current.type != EventType.Repaint ||
                sceneView == null ||
                sceneView.camera == null)
            {
                return;
            }

            List<RingBatch> batches = BuildRingBatches();
            if (batches.Count == 0)
            {
                return;
            }

            if (!EnsureMaterials())
            {
                return;
            }

            Camera camera = sceneView.camera;
            int width = Mathf.Max(1, camera.pixelWidth);
            int height = Mathf.Max(1, camera.pixelHeight);
            EnsureRenderTextures(width, height);

            Rect viewportRect = sceneView.cameraViewport;
            if (viewportRect.width <= 0f || viewportRect.height <= 0f)
            {
                viewportRect = new Rect(0f, 0f, width, height);
            }

            batches.Sort(CompareBatches);
            foreach (RingBatch batch in batches)
            {
                if (batch.Renderers.Count == 0)
                {
                    continue;
                }

                if (batch.RingIndex == 0 && TryDrawNativeOutline(batch))
                {
                    continue;
                }

                DrawSilhouetteRing(camera, viewportRect, batch);
            }
        }

        private static List<RingBatch> BuildRingBatches()
        {
            Dictionary<GameObject, List<RemoteSelection>> selectorsByObject =
                new Dictionary<GameObject, List<RemoteSelection>>();

            foreach (RemoteSelection selection in RemoteSelections.Values)
            {
                foreach (string objectId in selection.ObjectIds ?? new string[0])
                {
                    if (!UnitySyncSceneObjectRegistry.TryResolve(objectId, out GameObject gameObject) ||
                        gameObject == null)
                    {
                        continue;
                    }

                    if (!selectorsByObject.TryGetValue(gameObject, out List<RemoteSelection> selectors))
                    {
                        selectors = new List<RemoteSelection>();
                        selectorsByObject.Add(gameObject, selectors);
                    }

                    selectors.Add(selection);
                }
            }

            if (selectorsByObject.Count == 0)
            {
                return new List<RingBatch>();
            }

            HashSet<GameObject> localSelection = new HashSet<GameObject>(Selection.gameObjects);
            Dictionary<string, RingBatch> batchByKey = new Dictionary<string, RingBatch>();

            foreach (KeyValuePair<GameObject, List<RemoteSelection>> pair in selectorsByObject)
            {
                GameObject gameObject = pair.Key;
                List<RemoteSelection> selectors = pair.Value;
                selectors.Sort((left, right) => left.PlayerId.CompareTo(right.PlayerId));

                int localOffset = IsCoveredByLocalSelection(gameObject, localSelection) ? 1 : 0;
                for (int selectorIndex = 0; selectorIndex < selectors.Count; selectorIndex++)
                {
                    RemoteSelection selector = selectors[selectorIndex];
                    int ringIndex = localOffset + selectorIndex;
                    string batchKey = selector.PlayerId.ToString("N") + ":" + ringIndex;
                    if (!batchByKey.TryGetValue(batchKey, out RingBatch batch))
                    {
                        batch = new RingBatch
                        {
                            PlayerId = selector.PlayerId,
                            Color = selector.Color,
                            RingIndex = ringIndex
                        };
                        batchByKey.Add(batchKey, batch);
                    }

                    AddRenderers(gameObject, batch);
                }
            }

            return new List<RingBatch>(batchByKey.Values);
        }

        private static bool IsCoveredByLocalSelection(
            GameObject gameObject,
            HashSet<GameObject> localSelection)
        {
            Transform current = gameObject != null ? gameObject.transform : null;
            while (current != null)
            {
                if (localSelection.Contains(current.gameObject))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static void AddRenderers(GameObject gameObject, RingBatch batch)
        {
            Renderer[] renderers = gameObject.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null ||
                    !renderer.enabled ||
                    !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                int instanceId = renderer.GetInstanceID();
                if (batch.RendererIds.Add(instanceId))
                {
                    batch.Renderers.Add(renderer);
                }
            }
        }

        private static int CompareBatches(RingBatch left, RingBatch right)
        {
            int ringComparison = right.RingIndex.CompareTo(left.RingIndex);
            return ringComparison != 0
                ? ringComparison
                : left.PlayerId.CompareTo(right.PlayerId);
        }

        private static bool TryDrawNativeOutline(RingBatch batch)
        {
            if (NativeDrawOutlineMethod == null)
            {
                return false;
            }

            try
            {
                NativeDrawOutlineMethod.Invoke(
                    null,
                    new object[] { batch.Renderers.ToArray(), batch.Color, 0f });
                return true;
            }
            catch (TargetInvocationException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static MethodInfo FindNativeDrawOutlineMethod()
        {
            return typeof(Handles).GetMethod(
                "DrawOutline",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Renderer[]), typeof(Color), typeof(float) },
                null);
        }

        private static void DrawSilhouetteRing(
            Camera camera,
            Rect viewportRect,
            RingBatch batch)
        {
            RenderMask(camera, batch.Renderers);

            int innerRadius = batch.RingIndex <= 0
                ? 0
                : Mathf.RoundToInt(
                    BaseOutlinePixels + (batch.RingIndex - 1) * StackedOutlinePixels);
            int outerRadius = batch.RingIndex <= 0
                ? Mathf.RoundToInt(BaseOutlinePixels)
                : Mathf.RoundToInt(
                    BaseOutlinePixels + batch.RingIndex * StackedOutlinePixels);

            Dilate(_mask, _outerHorizontal, _outer, outerRadius);
            RenderTexture innerTexture;
            if (innerRadius <= 0)
            {
                innerTexture = _mask;
            }
            else
            {
                Dilate(_mask, _innerHorizontal, _inner, innerRadius);
                innerTexture = _inner;
            }

            _compositeMaterial.SetTexture("_InnerTex", innerTexture);
            _compositeMaterial.SetColor("_OutlineColor", batch.Color);

            Handles.BeginGUI();
            EditorGUI.DrawPreviewTexture(
                viewportRect,
                _outer,
                _compositeMaterial,
                ScaleMode.StretchToFill);
            Handles.EndGUI();
        }

        private static void RenderMask(Camera camera, List<Renderer> renderers)
        {
            CommandBuffer commandBuffer = new CommandBuffer
            {
                name = "UnitySync Selection Mask"
            };

            try
            {
                commandBuffer.SetRenderTarget(_maskHighResolution);
                commandBuffer.ClearRenderTarget(false, true, Color.clear);
                commandBuffer.SetViewProjectionMatrices(
                    camera.worldToCameraMatrix,
                    GL.GetGPUProjectionMatrix(camera.projectionMatrix, true));

                foreach (Renderer renderer in renderers)
                {
                    int submeshCount = GetSubmeshCount(renderer);
                    for (int submeshIndex = 0; submeshIndex < submeshCount; submeshIndex++)
                    {
                        commandBuffer.DrawRenderer(renderer, _maskMaterial, submeshIndex, 0);
                    }
                }

                Graphics.ExecuteCommandBuffer(commandBuffer);
            }
            finally
            {
                commandBuffer.Release();
            }

            if (_supersample > 1)
            {
                Graphics.Blit(_maskHighResolution, _mask);
            }
        }

        private static int GetSubmeshCount(Renderer renderer)
        {
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer skinnedMeshRenderer)
            {
                mesh = skinnedMeshRenderer.sharedMesh;
            }
            else if (renderer is MeshRenderer)
            {
                MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            }

            if (mesh != null)
            {
                return Mathf.Max(1, mesh.subMeshCount);
            }

            Material[] materials = renderer.sharedMaterials;
            return Mathf.Max(1, materials != null ? materials.Length : 1);
        }

        private static void Dilate(
            RenderTexture source,
            RenderTexture horizontal,
            RenderTexture destination,
            int radius)
        {
            _dilateMaterial.SetFloat("_Radius", Mathf.Max(0, radius));
            _dilateMaterial.SetVector("_Direction", new Vector4(1f, 0f, 0f, 0f));
            Graphics.Blit(source, horizontal, _dilateMaterial);

            _dilateMaterial.SetVector("_Direction", new Vector4(0f, 1f, 0f, 0f));
            Graphics.Blit(horizontal, destination, _dilateMaterial);
        }

        private static bool EnsureMaterials()
        {
            if (_maskMaterial != null && _dilateMaterial != null && _compositeMaterial != null)
            {
                return true;
            }

            Shader maskShader = Shader.Find("Hidden/Glasspage/UnitySync/SelectionMask");
            Shader dilateShader = Shader.Find("Hidden/Glasspage/UnitySync/SelectionDilate");
            Shader compositeShader = Shader.Find("Hidden/Glasspage/UnitySync/SelectionComposite");
            if (maskShader == null || dilateShader == null || compositeShader == null)
            {
                return false;
            }

            _maskMaterial = new Material(maskShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _dilateMaterial = new Material(dilateShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _compositeMaterial = new Material(compositeShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            return true;
        }

        private static void EnsureRenderTextures(int width, int height)
        {
            int supersample = width * height <= SupersamplePixelLimit ? MaskSupersample : 1;
            if (_mask != null &&
                _textureWidth == width &&
                _textureHeight == height &&
                _supersample == supersample)
            {
                return;
            }

            ReleaseRenderTextures();
            _textureWidth = width;
            _textureHeight = height;
            _supersample = supersample;

            _maskHighResolution = CreateRenderTexture(
                width * supersample,
                height * supersample,
                "UnitySync Selection Mask High Resolution");
            _mask = supersample > 1
                ? CreateRenderTexture(width, height, "UnitySync Selection Mask")
                : _maskHighResolution;
            _outerHorizontal = CreateRenderTexture(width, height, "UnitySync Selection Outer Horizontal");
            _outer = CreateRenderTexture(width, height, "UnitySync Selection Outer");
            _innerHorizontal = CreateRenderTexture(width, height, "UnitySync Selection Inner Horizontal");
            _inner = CreateRenderTexture(width, height, "UnitySync Selection Inner");
        }

        private static RenderTexture CreateRenderTexture(int width, int height, string name)
        {
            RenderTextureFormat format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8)
                ? RenderTextureFormat.R8
                : RenderTextureFormat.ARGB32;
            RenderTexture texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            texture.Create();
            return texture;
        }

        private static void ReleaseResources()
        {
            ReleaseRenderTextures();
            DestroyMaterial(ref _maskMaterial);
            DestroyMaterial(ref _dilateMaterial);
            DestroyMaterial(ref _compositeMaterial);
        }

        private static void ReleaseRenderTextures()
        {
            RenderTexture highResolution = _maskHighResolution;
            RenderTexture mask = _mask;

            _maskHighResolution = null;
            _mask = null;
            DestroyRenderTexture(highResolution);
            if (mask != highResolution)
            {
                DestroyRenderTexture(mask);
            }

            DestroyRenderTexture(_outerHorizontal);
            DestroyRenderTexture(_outer);
            DestroyRenderTexture(_innerHorizontal);
            DestroyRenderTexture(_inner);
            _outerHorizontal = null;
            _outer = null;
            _innerHorizontal = null;
            _inner = null;
            _textureWidth = 0;
            _textureHeight = 0;
            _supersample = 1;
        }

        private static void DestroyRenderTexture(RenderTexture texture)
        {
            if (texture == null)
            {
                return;
            }

            texture.Release();
            UnityEngine.Object.DestroyImmediate(texture);
        }

        private static void DestroyMaterial(ref Material material)
        {
            if (material == null)
            {
                return;
            }

            UnityEngine.Object.DestroyImmediate(material);
            material = null;
        }
    }
}
