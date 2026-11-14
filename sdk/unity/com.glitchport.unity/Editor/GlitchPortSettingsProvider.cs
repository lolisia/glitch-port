#nullable enable
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GlitchPort.Unity.Editor
{
    /// <summary>
    /// Adds the <c>Project Settings &gt; Glitch Port</c> page. The page creates
    /// the project's <see cref="GlitchPortSettings"/> asset on first use, edits
    /// every setting through <see cref="SerializedObject"/>, and validates the
    /// DSN.
    /// </summary>
    public sealed class GlitchPortSettingsProvider : SettingsProvider
    {
        /// <summary>The project folder that owns the generated settings asset.</summary>
        internal const string SettingsFolder = "Assets/GlitchPort";

        /// <summary>The folder the runtime loads the settings asset from.</summary>
        internal const string ResourcesFolder = SettingsFolder + "/Resources";

        /// <summary>The file name the runtime uses with <c>Resources.Load</c>.</summary>
        internal const string SettingsResourceName = "GlitchPortSettings";

        /// <summary>The project-relative path of the settings asset.</summary>
        internal const string SettingsAssetPath = "Assets/GlitchPort/Resources/GlitchPortSettings.asset";

        private const string ProviderPath = "Project/GlitchPort";
        private const string ProviderLabel = "Glitch Port";
        private const string IntroductionText = "DSN 만 입력하면 자동으로 동작합니다";
        private const string MissingAssetText =
            "GlitchPortSettings.asset was not found. Create it to configure the Unity SDK:";
        private const string InvalidDsnText =
            "DSN 형식이 올바르지 않습니다. 예: http://public@localhost:5080/1";
        private const string CreateAssetText = "Create settings asset";

        private static readonly string[] SearchKeywords =
        {
            "glitch",
            "port",
            "dsn",
            "sentry",
            "error",
            "crash",
            "relay",
            "terminal",
            "log",
        };

        private GlitchPortSettings? _settings;
        private SerializedObject? _serializedSettings;

        private GlitchPortSettingsProvider(string path, SettingsScope scope)
            : base(path, scope, SearchKeywords)
        {
        }

        /// <summary>Creates the Project Settings provider.</summary>
        /// <returns>The registered settings provider.</returns>
        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            var provider = new GlitchPortSettingsProvider(ProviderPath, SettingsScope.Project)
            {
                label = ProviderLabel,
            };
            provider.activateHandler = provider.ActivateProvider;
            provider.guiHandler = provider.DrawGui;
            return provider;
        }

        /// <summary>
        /// Returns the settings asset, creating it (and the
        /// <c>Assets/GlitchPort/Resources</c> folders) when it does not exist.
        /// </summary>
        /// <returns>The settings asset, or <see langword="null"/> when creation failed.</returns>
        internal static GlitchPortSettings? EnsureSettingsAsset()
        {
            var settings = AssetDatabase.LoadAssetAtPath<GlitchPortSettings>(SettingsAssetPath);
            if (settings != null)
            {
                return settings;
            }

            var created = ScriptableObject.CreateInstance<GlitchPortSettings>();
            try
            {
                EnsureFolder(SettingsFolder);
                EnsureFolder(ResourcesFolder);

                AssetDatabase.CreateAsset(created, SettingsAssetPath);
                AssetDatabase.SaveAssets();
                return created;
            }
            catch (Exception exception)
            {
                UnityEngine.Object.DestroyImmediate(created);
                Debug.LogWarning(
                    "[GlitchPort] Could not create " + SettingsAssetPath + ": " + exception.Message);
                return null;
            }
        }

        private void ActivateProvider(string searchContext, VisualElement rootElement)
        {
            // Opening the page is the documented moment the asset appears, so
            // a project that only installed the package still gets a settings
            // object to edit.
            SetSettings(EnsureSettingsAsset());
        }

        private void DrawGui(string searchContext)
        {
            EditorGUILayout.HelpBox(IntroductionText, MessageType.Info);

            var settings = LoadSettingsAsset();
            if (settings == null)
            {
                EditorGUILayout.HelpBox(MissingAssetText, MessageType.Warning);
                if (GUILayout.Button(CreateAssetText))
                {
                    settings = EnsureSettingsAsset();
                    SetSettings(settings);
                }
            }

            if (settings == null)
            {
                return;
            }

            if (_serializedSettings == null || _serializedSettings.targetObject != settings)
            {
                _serializedSettings = new SerializedObject(settings);
            }

            _serializedSettings.Update();

            DrawGeneralSection(settings);
            DrawCaptureSection();
            DrawLocalRelaySection();
            DrawDiagnosticsSection();

            // PropertyField writes changes into the SerializedObject; apply them
            // once so one undo step covers the whole frame.
            _serializedSettings.ApplyModifiedProperties();
        }

        private void DrawGeneralSection(GlitchPortSettings settings)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("General", EditorStyles.boldLabel);

            DrawProperty("Enabled");
            DrawProperty("Dsn", "DSN");

            var dsnProperty = FindProperty("Dsn");
            var dsn = dsnProperty != null ? dsnProperty.stringValue : settings.Dsn;
            if (!string.IsNullOrWhiteSpace(dsn) && !Dsn.TryParse(dsn, out _))
            {
                EditorGUILayout.HelpBox(InvalidDsnText, MessageType.Error);
            }

            DrawProperty("AutoInitialize");
            DrawProperty("HttpMinLevel");
        }

        private void DrawCaptureSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Log capture", EditorStyles.boldLabel);

            DrawProperty("CaptureExceptions");
            DrawProperty("CaptureErrors");
            DrawProperty("CaptureWarnings");
            DrawProperty("CaptureLogs");
        }

        private void DrawLocalRelaySection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Local relays", EditorStyles.boldLabel);

            DrawProperty("TerminalMode");
            DrawProperty("TerminalWindow");
            DrawProperty("FileMode");
            DrawProperty("FileDirectory");
            DrawProperty("FileMinLevel");
        }

        private void DrawDiagnosticsSection()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);

            DrawProperty("DebugLogging");
        }

        private void DrawProperty(string propertyName, string? label = null)
        {
            var property = FindProperty(propertyName);
            if (property == null)
            {
                return;
            }

            var content = new GUIContent(label ?? property.displayName);
            EditorGUILayout.PropertyField(property, content, true);
        }

        private SerializedProperty? FindProperty(string propertyName)
        {
            if (_serializedSettings == null)
            {
                return null;
            }

            return _serializedSettings.FindProperty(propertyName);
        }

        private GlitchPortSettings? LoadSettingsAsset()
        {
            if (_settings != null)
            {
                return _settings;
            }

            SetSettings(AssetDatabase.LoadAssetAtPath<GlitchPortSettings>(SettingsAssetPath));
            return _settings;
        }

        private void SetSettings(GlitchPortSettings? settings)
        {
            _settings = settings;
            _serializedSettings = settings != null ? new SerializedObject(settings) : null;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            var parent = Path.GetDirectoryName(folder);
            if (string.IsNullOrEmpty(parent))
            {
                return;
            }

            parent = parent!.Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
