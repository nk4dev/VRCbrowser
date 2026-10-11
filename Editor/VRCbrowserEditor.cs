using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Inspector for VRCbrowser: the settings a world author edits (pages, bookmarks, appearance) come first, and the
/// references that wire up the prefab's UI are folded away at the bottom.
/// </summary>
[CustomEditor(typeof(VRCbrowser))]
public class VRCbrowserEditor : Editor
{
    private const int DefaultMaxBookmarks = 8;

    private static readonly string[] PageFields = { "homeUrl", "welcomePage" };
    private static readonly string[] FrameFields = { "frameUrls" };
    private static readonly string[] AppearanceFields = { "imageMaxSize", "baseFontSize", "sidePadding", "plainTextColor" };
    private static readonly string[] LimitFields = { "maxLines", "maxCharsPerBlock", "maxHtmlChars", "maxHtmlTags" };
    private static readonly string[] ScriptFields = { "js" };
    private static readonly string[] BookmarkFields = { "bookmarkUrls", "bookmarkLabels" };

    private static bool showAppearance = false;
    private static bool showLimits = false;
    private static bool showReferences = false;

    public override void OnInspectorGUI()
    {
        if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;

        serializedObject.Update();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Pages", EditorStyles.boldLabel);
        DrawFields(PageFields);

        EditorGUILayout.Space();
        DrawBookmarks();

        if (serializedObject.FindProperty(FrameFields[0]) != null)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Frames", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Pages a <frameset> may load. A frame loads only when its URL is listed here, in the bookmarks or as the home URL.", MessageType.None);
            DrawFields(FrameFields);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Scripts", EditorStyles.boldLabel);
        DrawFields(ScriptFields);

        EditorGUILayout.Space();
        showAppearance = EditorGUILayout.Foldout(showAppearance, "Appearance", true);
        if (showAppearance) DrawIndented(AppearanceFields);
        showLimits = EditorGUILayout.Foldout(showLimits, "Limits", true);
        if (showLimits) DrawIndented(LimitFields);
        showReferences = EditorGUILayout.Foldout(showReferences, "UI References (prefab wiring)", true);
        if (showReferences) DrawRemaining();

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawBookmarks()
    {
        SerializedProperty urls = serializedObject.FindProperty("bookmarkUrls");
        SerializedProperty labels = serializedObject.FindProperty("bookmarkLabels");
        SerializedProperty buttons = serializedObject.FindProperty("bookmarkButtons");
        int max = buttons != null && buttons.arraySize > 0 ? buttons.arraySize : DefaultMaxBookmarks;

        // the two arrays are edited as one list of rows
        if (labels.arraySize != urls.arraySize) labels.arraySize = urls.arraySize;
        int count = urls.arraySize;

        EditorGUILayout.LabelField("Bookmarks (" + count + " / " + max + ")", EditorStyles.boldLabel);
        if (count == 0) EditorGUILayout.HelpBox("No bookmarks. The menu button is hidden in the world.", MessageType.Info);
        if (count > max) EditorGUILayout.HelpBox("Only the first " + max + " bookmarks have a button.", MessageType.Warning);

        int remove = -1;
        int moveFrom = -1;
        int moveTo = -1;
        for (int i = 0; i < count; i++)
        {
            SerializedProperty url = urls.GetArrayElementAtIndex(i).FindPropertyRelative("url");
            SerializedProperty label = labels.GetArrayElementAtIndex(i);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField((i + 1).ToString(), GUILayout.Width(18f));
            label.stringValue = EditorGUILayout.TextField(label.stringValue);
            using (new EditorGUI.DisabledScope(i == 0))
            {
                if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(24f))) { moveFrom = i; moveTo = i - 1; }
            }
            using (new EditorGUI.DisabledScope(i == count - 1))
            {
                if (GUILayout.Button("▼", EditorStyles.miniButtonMid, GUILayout.Width(24f))) { moveFrom = i; moveTo = i + 1; }
            }
            if (GUILayout.Button("✕", EditorStyles.miniButtonRight, GUILayout.Width(24f))) remove = i;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("URL", GUILayout.Width(32f));
            url.stringValue = EditorGUILayout.TextField(url.stringValue).Trim();
            EditorGUILayout.EndHorizontal();
            if (url.stringValue.Length == 0) EditorGUILayout.HelpBox("Empty URL: this row is not shown in the menu.", MessageType.Warning);
            EditorGUILayout.EndVertical();
        }

        if (moveFrom >= 0)
        {
            urls.MoveArrayElement(moveFrom, moveTo);
            labels.MoveArrayElement(moveFrom, moveTo);
            GUI.FocusControl(null);
        }
        if (remove >= 0)
        {
            urls.DeleteArrayElementAtIndex(remove);
            labels.DeleteArrayElementAtIndex(remove);
            GUI.FocusControl(null);
        }

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(count >= max))
        {
            if (GUILayout.Button("Add Bookmark"))
            {
                // a new array element copies the previous one, so clear it
                urls.arraySize = count + 1;
                labels.arraySize = count + 1;
                urls.GetArrayElementAtIndex(count).FindPropertyRelative("url").stringValue = "";
                labels.GetArrayElementAtIndex(count).stringValue = "";
            }
        }
        using (new EditorGUI.DisabledScope(count == 0))
        {
            if (GUILayout.Button("Clear", GUILayout.Width(60f))
                && EditorUtility.DisplayDialog("VRCbrowser", "Remove all bookmarks?", "Remove", "Cancel"))
            {
                urls.arraySize = 0;
                labels.arraySize = 0;
                GUI.FocusControl(null);
            }
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawFields(string[] names)
    {
        for (int i = 0; i < names.Length; i++)
        {
            SerializedProperty property = serializedObject.FindProperty(names[i]);
            if (property != null) EditorGUILayout.PropertyField(property, true);
        }
    }

    private void DrawIndented(string[] names)
    {
        EditorGUI.indentLevel++;
        DrawFields(names);
        EditorGUI.indentLevel--;
    }

    // everything not shown above: the references to the prefab's own UI objects
    private void DrawRemaining()
    {
        EditorGUI.indentLevel++;
        EditorGUILayout.HelpBox("These point at objects inside the prefab. They only need changing if you rebuild its UI.", MessageType.None);
        SerializedProperty property = serializedObject.GetIterator();
        bool enterChildren = true;
        while (property.NextVisible(enterChildren))
        {
            enterChildren = false;
            string name = property.name;
            if (name == "m_Script" || Contains(PageFields, name) || Contains(BookmarkFields, name) || Contains(FrameFields, name)
                || Contains(AppearanceFields, name) || Contains(LimitFields, name) || Contains(ScriptFields, name)) continue;
            EditorGUILayout.PropertyField(property, true);
        }
        EditorGUI.indentLevel--;
    }

    private static bool Contains(string[] names, string name)
    {
        return System.Array.IndexOf(names, name) >= 0;
    }
}
