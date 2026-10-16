#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using WanosAudio;

namespace Game
{
    /// <summary>
    /// Editor-only debug visualization for AmbienceZone sound sources.
    /// Shows all emitters, their radii, forest trees, and listener position.
    /// </summary>
    public class AmbienceZoneDebugWindow : EditorWindow
    {
        private bool _showForests = true;
        private bool _showRivers = true;
        private bool _showWaterfalls = true;
        private bool _showCampfires = true;
        private bool _showOceans = true;
        private bool _showLakes = true;
        private bool _showGrasslands = true;
        private bool _showPlateaus = true;

        // Visual settings
        private float _emitterScale = 1f;
        private Color _forestColor = new Color(0.2f, 0.8f, 0.2f, 0.3f);
        private Color _riverColor = new Color(0.2f, 0.5f, 0.9f, 0.3f);
        private Color _waterfallColor = new Color(0.3f, 0.6f, 1f, 0.3f);
        private Color _campfireColor = new Color(1f, 0.5f, 0.2f, 0.3f);
        private Color _oceanColor = new Color(0.1f, 0.3f, 0.6f, 0.3f);
        private Color _lakeColor = new Color(0.1f, 0.5f, 0.7f, 0.3f);
        private Color _grasslandColor = new Color(0.6f, 0.8f, 0.2f, 0.3f);
        private Color _plateauColor = new Color(0.7f, 0.5f, 0.3f, 0.3f);
        private Color _listenerColor = new Color(1f, 0f, 0f, 0.9f);

        private Vector3 _editorListenerPos = new Vector3(588.44f, 12.71f, 674.97f);
        private bool _useEditorListenerPos;
        private bool _showDistanceLabels = true;

        [MenuItem("Tools/Ambience Zone Debug")]
        public static void ShowWindow()
        {
            var window = GetWindow<AmbienceZoneDebugWindow>("Ambience Debug");
            window.minSize = new Vector2(300, 400);
        }

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        void OnGUI()
        {
            titleContent = new GUIContent("Ambience Debug", EditorGUIUtility.IconContent("d_AudioSource Icon").image);

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Ambience Zone Debug", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            // Listener position
            EditorGUILayout.LabelField("Listener Position", EditorStyles.boldLabel);
            _useEditorListenerPos = EditorGUILayout.Toggle("Use Editor Position", _useEditorListenerPos);
            if (_useEditorListenerPos)
            {
                _editorListenerPos = EditorGUILayout.Vector3Field("Editor Listener", _editorListenerPos);
            }
            else
            {
                EditorGUILayout.HelpBox("Runtime listener position will be used.", MessageType.Info);
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Visualization Settings", EditorStyles.boldLabel);
            _emitterScale = EditorGUILayout.Slider("Emitter Scale", _emitterScale, 0.1f, 5f);
            _showDistanceLabels = EditorGUILayout.Toggle("Show Distance Labels", _showDistanceLabels);

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Emitter Types", EditorStyles.boldLabel);

            _showForests = EditorGUILayout.Foldout(_showForests, "Forest Beds", true);
            if (_showForests)
            {
                EditorGUI.indentLevel++;
                _forestColor = EditorGUILayout.ColorField("Color", _forestColor);
                EditorGUI.indentLevel--;
            }

            _showRivers = EditorGUILayout.Foldout(_showRivers, "Rivers", true);
            if (_showRivers)
            {
                EditorGUI.indentLevel++;
                _riverColor = EditorGUILayout.ColorField("River Color", _riverColor);
                EditorGUI.indentLevel--;
            }

            _showWaterfalls = EditorGUILayout.Foldout(_showWaterfalls, "Waterfalls", true);
            if (_showWaterfalls)
            {
                EditorGUI.indentLevel++;
                _waterfallColor = EditorGUILayout.ColorField("Waterfall Color", _waterfallColor);
                EditorGUI.indentLevel--;
            }

            _showCampfires = EditorGUILayout.Foldout(_showCampfires, "Campfires", true);
            if (_showCampfires)
            {
                EditorGUI.indentLevel++;
                _campfireColor = EditorGUILayout.ColorField("Campfire Color", _campfireColor);
                EditorGUI.indentLevel--;
            }

            _showOceans = EditorGUILayout.Foldout(_showOceans, "Oceans", true);
            if (_showOceans)
            {
                EditorGUI.indentLevel++;
                _oceanColor = EditorGUILayout.ColorField("Ocean Color", _oceanColor);
                EditorGUI.indentLevel--;
            }

            _showLakes = EditorGUILayout.Foldout(_showLakes, "Lakes", true);
            if (_showLakes)
            {
                EditorGUI.indentLevel++;
                _lakeColor = EditorGUILayout.ColorField("Lake Color", _lakeColor);
                EditorGUI.indentLevel--;
            }

            _showGrasslands = EditorGUILayout.Foldout(_showGrasslands, "Grasslands", true);
            if (_showGrasslands)
            {
                EditorGUI.indentLevel++;
                _grasslandColor = EditorGUILayout.ColorField("Grassland Color", _grasslandColor);
                EditorGUI.indentLevel--;
            }

            _showPlateaus = EditorGUILayout.Foldout(_showPlateaus, "Plateaus", true);
            if (_showPlateaus)
            {
                EditorGUI.indentLevel++;
                _plateauColor = EditorGUILayout.ColorField("Plateau Color", _plateauColor);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(10);
            if (GUILayout.Button("Refresh Scene View"))
            {
                SceneView.RepaintAll();
            }
        }

        void OnSceneGUI(SceneView sceneView)
        {
            if (!Camera.main)
                return;

            var listenerPos = GetListenerPosition();

            // Draw listener position
            if (_useEditorListenerPos || !Application.isPlaying)
            {
                Handles.color = _listenerColor;
                Handles.SphereHandleCap(0, listenerPos, Quaternion.identity, 3f, EventType.Repaint);
                DrawLabel(listenerPos + Vector3.up * 3f, "Listener", _listenerColor);
            }

            // Draw all static emitters
            foreach (var spec in AmbienceZoneCatalog.StaticEmitters)
            {
                if (!ShouldShowEmiterKind(spec.Kind))
                    continue;

                Color color = GetColorForKind(spec.Kind);
                DrawEmitter(spec, listenerPos, color);
            }

            // Draw native rivers
            DrawNativeRivers(listenerPos);

            // Always repaint when playing
            if (Application.isPlaying)
            {
                Repaint();
            }
        }

        Vector3 GetListenerPosition()
        {
            if (_useEditorListenerPos || !Application.isPlaying)
                return _editorListenerPos;

            // Try to get runtime listener position
            try
            {
                if (WanosAudio.WanosInteraction.TryGetListenerPosition(out Vector3 pos))
                    return pos;
            }
            catch { }

            return _editorListenerPos;
        }

        bool ShouldShowEmiterKind(AmbienceEmitterKind kind)
        {
            return kind switch
            {
                AmbienceEmitterKind.Forest => _showForests,
                AmbienceEmitterKind.River => _showRivers,
                AmbienceEmitterKind.Waterfall => _showWaterfalls,
                AmbienceEmitterKind.Campfire => _showCampfires,
                AmbienceEmitterKind.Ocean => _showOceans,
                AmbienceEmitterKind.Lake => _showLakes,
                AmbienceEmitterKind.Grassland => _showGrasslands,
                AmbienceEmitterKind.Plateau => _showPlateaus,
                _ => true
            };
        }

        Color GetColorForKind(AmbienceEmitterKind kind)
        {
            return kind switch
            {
                AmbienceEmitterKind.Forest => _forestColor,
                AmbienceEmitterKind.River => _riverColor,
                AmbienceEmitterKind.Waterfall => _waterfallColor,
                AmbienceEmitterKind.Campfire => _campfireColor,
                AmbienceEmitterKind.Ocean => _oceanColor,
                AmbienceEmitterKind.Lake => _lakeColor,
                AmbienceEmitterKind.Grassland => _grasslandColor,
                AmbienceEmitterKind.Plateau => _plateauColor,
                _ => Color.gray
            };
        }

        void DrawEmitter(AmbienceEmitterSpec spec, Vector3 listenerPos, Color color)
        {
            float enterRadius = spec.EnterRadius * _emitterScale;
            float exitRadius = spec.ExitRadius * _emitterScale;

            // Draw enter radius (solid)
            Handles.color = color;
            Handles.DrawWireDisc(spec.Position, Vector3.up, enterRadius);

            // Draw exit radius (dashed)
            Handles.color = new Color(color.r, color.g, color.b, color.a * 0.5f);
            Handles.DrawWireDisc(spec.Position, Vector3.up, exitRadius);

            // Draw emitter point
            Handles.SphereHandleCap(0, spec.Position, Quaternion.identity, 2f, EventType.Repaint);

            // Draw label
            string label = $"{spec.Id}\nR={enterRadius:F0}m";
            float dist = GetDistance(spec, listenerPos);
            if (_showDistanceLabels)
            {
                label += $"\nDist={dist:F1}m";
            }
            DrawLabel(spec.Position + Vector3.up * 2f, label, color);

            // Draw line to listener
            Handles.color = new Color(color.r, color.g, color.b, 0.3f);
            Handles.DrawLine(spec.Position, listenerPos);
        }

        IReadOnlyList<MapItemData> GetMapItems()
        {
            try
            {
                var spawner = GameEntry.World?.MapItemSpawner;
                if (spawner != null)
                    return spawner.AllMapItems;
            }
            catch { }
            return null;
        }

        void DrawNativeRivers(Vector3 listenerPos)
        {
            AmbienceZoneCatalog.AppendNativeRivers(spec =>
            {
                var color = GetColorForKind(spec.Kind);
                DrawEmitter(spec, listenerPos, color);
            });
        }

        float GetDistance(AmbienceEmitterSpec spec, Vector3 listenerPos)
        {
            if (spec.UseXzDistance)
            {
                float dx = listenerPos.x - spec.Position.x;
                float dz = listenerPos.z - spec.Position.z;
                return Mathf.Sqrt(dx * dx + dz * dz);
            }
            return Vector3.Distance(listenerPos, spec.Position);
        }

        void DrawLabel(Vector3 position, string text, Color color)
        {
            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = color },
                fontSize = 10,
                alignment = TextAnchor.MiddleCenter
            };
            style.contentOffset = Vector2.zero;

            // Calculate label size
            var content = new GUIContent(text);
            Vector2 size = style.CalcSize(content);
            Vector2 worldPos = HandleUtility.WorldToGUIPoint(position);

            // Draw background
            var rect = new Rect(worldPos.x - size.x / 2 - 2, worldPos.y - size.y / 2 - 2, size.x + 4, size.y + 4);
            var bgStyle = new GUIStyle(EditorStyles.helpBox);
            bgStyle.normal.background = MakeTex(new Color(0, 0, 0, 0.7f));
            EditorGUI.DropShadowLabel(rect, content, style);
        }

        Texture2D MakeTex(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }
    }
}
#endif
