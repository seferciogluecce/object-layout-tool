using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

internal enum ObjectLayoutAxis
{
    X,
    Y,
    Z
}

internal enum ObjectLayoutOrder
{
    AxisPosition,
    HierarchyOrder
}

internal enum ObjectSpacingMode
{
    Pack,
    EvenlyDistribute
}

internal enum ObjectLayoutSource
{
    RendererBounds,
    TransformPosition,
    ColliderBounds
}

internal static class ObjectLayoutTools
{
    private const string WindowMenuPath = "Tools/Object Layout Tool";

    [MenuItem(WindowMenuPath, false, 209)]
    private static void OpenWindow()
    {
        ObjectLayoutWindow.ShowWindow();
    }

    internal static bool CanLayoutParentChildren(Transform parent)
    {
        return !EditorApplication.isPlayingOrWillChangePlaymode && parent != null && parent.childCount > 1;
    }

    internal static bool CanLayoutSelection()
    {
        return !EditorApplication.isPlayingOrWillChangePlaymode && Selection.transforms.Length > 1;
    }

    internal static void LayoutParentChildren(
        Transform parent,
        ObjectLayoutAxis axis,
        ObjectLayoutOrder order,
        ObjectSpacingMode spacingMode,
        float spacing)
    {
        LayoutParentChildren(parent, axis, order, spacingMode, spacing, ObjectLayoutSource.RendererBounds);
    }

    internal static void LayoutParentChildren(
        Transform parent,
        ObjectLayoutAxis axis,
        ObjectLayoutOrder order,
        ObjectSpacingMode spacingMode,
        float spacing,
        ObjectLayoutSource source)
    {
        if (parent == null)
            return;

        var children = new List<Transform>();
        for (int i = 0; i < parent.childCount; i++)
            children.Add(parent.GetChild(i));

        SortTransforms(children, axis, order);
        LayoutTransforms(children, $"Layout Children On {axis}", axis, spacingMode, spacing, source);
    }

    internal static void LayoutSelection(ObjectLayoutAxis axis, ObjectLayoutOrder order, ObjectSpacingMode spacingMode, float spacing)
    {
        LayoutSelection(axis, order, spacingMode, spacing, ObjectLayoutSource.RendererBounds);
    }

    internal static void LayoutSelection(
        ObjectLayoutAxis axis,
        ObjectLayoutOrder order,
        ObjectSpacingMode spacingMode,
        float spacing,
        ObjectLayoutSource source)
    {
        LayoutTransforms(GetTopLevelSelection(axis, order), $"Layout Top-Level Selection On {axis}", axis, spacingMode, spacing, source);
    }

    private static void LayoutTransforms(
        List<Transform> transforms,
        string undoName,
        ObjectLayoutAxis axis,
        ObjectSpacingMode spacingMode,
        float spacing,
        ObjectLayoutSource source)
    {
        if (transforms.Count < 2)
            return;

        Physics.SyncTransforms();

        List<LayoutItem> layoutItems = CollectLayoutItems(transforms, source);
        if (layoutItems.Count < 2)
            return;

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(undoName);

        Object[] undoObjects = new Object[layoutItems.Count];
        for (int i = 0; i < layoutItems.Count; i++)
            undoObjects[i] = layoutItems[i].Transform;
        Undo.RecordObjects(undoObjects, undoName);

        if (spacingMode == ObjectSpacingMode.EvenlyDistribute)
            EvenlyDistributeItems(layoutItems, axis);
        else
            PackItems(layoutItems, axis, spacing);

        Undo.CollapseUndoOperations(undoGroup);
        SceneView.RepaintAll();
    }

    private static List<LayoutItem> CollectLayoutItems(List<Transform> transforms, ObjectLayoutSource source)
    {
        var layoutItems = new List<LayoutItem>(transforms.Count);
        foreach (Transform transform in transforms)
        {
            if (!TryGetBounds(transform, source, out Bounds bounds))
            {
                Debug.Log($"Skipping '{transform.name}': no active {GetSourceDescription(source)} found.", transform);
                continue;
            }

            layoutItems.Add(new LayoutItem(transform, bounds));
        }

        return layoutItems;
    }

    private static void PackItems(List<LayoutItem> layoutItems, ObjectLayoutAxis axis, float spacing)
    {
        float nextMin = GetBoundsMax(layoutItems[0].Bounds, axis) + spacing;
        for (int i = 1; i < layoutItems.Count; i++)
            nextMin = MoveItemToMin(layoutItems[i], axis, nextMin) + spacing;
    }

    private static void EvenlyDistributeItems(List<LayoutItem> layoutItems, ObjectLayoutAxis axis)
    {
        float spanMin = GetBoundsMin(layoutItems[0].Bounds, axis);
        float spanMax = GetBoundsMax(layoutItems[layoutItems.Count - 1].Bounds, axis);
        float totalSize = 0f;

        foreach (LayoutItem item in layoutItems)
            totalSize += GetBoundsSize(item.Bounds, axis);

        float availableSpacing = spanMax - spanMin - totalSize;
        float distributedSpacing = layoutItems.Count > 1 ? availableSpacing / (layoutItems.Count - 1) : 0f;

        float nextMin = spanMin;
        for (int i = 0; i < layoutItems.Count; i++)
            nextMin = MoveItemToMin(layoutItems[i], axis, nextMin) + distributedSpacing;
    }

    private static float MoveItemToMin(LayoutItem item, ObjectLayoutAxis axis, float targetMin)
    {
        Transform transform = item.Transform;
        Bounds bounds = item.Bounds;
        Vector3 position = transform.position;
        float offset = targetMin - GetBoundsMin(bounds, axis);
        SetAxisValue(ref position, axis, GetAxisValue(position, axis) + offset);
        transform.position = position;
        Physics.SyncTransforms();
        EditorUtility.SetDirty(transform);

        if (transform.gameObject.scene.IsValid())
            EditorSceneManager.MarkSceneDirty(transform.gameObject.scene);

        return GetBoundsMax(OffsetBounds(bounds, axis, offset), axis);
    }

    private static List<Transform> GetTopLevelSelection(ObjectLayoutAxis axis, ObjectLayoutOrder order)
    {
        Transform[] selection = Selection.transforms;
        var selectedSet = new HashSet<Transform>(selection);
        var result = new List<Transform>(selection.Length);

        foreach (Transform transform in selection)
        {
            bool hasSelectedAncestor = false;
            for (Transform ancestor = transform.parent; ancestor != null; ancestor = ancestor.parent)
            {
                if (!selectedSet.Contains(ancestor))
                    continue;

                hasSelectedAncestor = true;
                break;
            }

            if (!hasSelectedAncestor)
                result.Add(transform);
        }

        SortTransforms(result, axis, order);
        return result;
    }

    private static void SortTransforms(List<Transform> transforms, ObjectLayoutAxis axis, ObjectLayoutOrder order)
    {
        transforms.Sort((left, right) => CompareTransformsForLayout(left, right, axis, order));
    }

    private static int CompareTransformsForLayout(
        Transform left,
        Transform right,
        ObjectLayoutAxis axis,
        ObjectLayoutOrder order)
    {
        if (order == ObjectLayoutOrder.HierarchyOrder)
            return string.CompareOrdinal(GetHierarchyPath(left), GetHierarchyPath(right));

        int axisCompare = GetAxisValue(left.position, axis).CompareTo(GetAxisValue(right.position, axis));
        if (axisCompare != 0)
            return axisCompare;

        return string.CompareOrdinal(GetHierarchyPath(left), GetHierarchyPath(right));
    }

    private static string GetHierarchyPath(Transform transform)
    {
        string path = transform.GetSiblingIndex().ToString("D6") + ":" + transform.name;
        for (Transform parent = transform.parent; parent != null; parent = parent.parent)
            path = parent.GetSiblingIndex().ToString("D6") + ":" + parent.name + "/" + path;

        return path;
    }

    private static float GetAxisValue(Vector3 value, ObjectLayoutAxis axis)
    {
        switch (axis)
        {
            case ObjectLayoutAxis.X:
                return value.x;
            case ObjectLayoutAxis.Y:
                return value.y;
            default:
                return value.z;
        }
    }

    private static void SetAxisValue(ref Vector3 value, ObjectLayoutAxis axis, float axisValue)
    {
        switch (axis)
        {
            case ObjectLayoutAxis.X:
                value.x = axisValue;
                break;
            case ObjectLayoutAxis.Y:
                value.y = axisValue;
                break;
            default:
                value.z = axisValue;
                break;
        }
    }

    private static float GetBoundsMin(Bounds bounds, ObjectLayoutAxis axis)
    {
        return GetAxisValue(bounds.min, axis);
    }

    private static float GetBoundsMax(Bounds bounds, ObjectLayoutAxis axis)
    {
        return GetAxisValue(bounds.max, axis);
    }

    private static float GetBoundsSize(Bounds bounds, ObjectLayoutAxis axis)
    {
        return GetBoundsMax(bounds, axis) - GetBoundsMin(bounds, axis);
    }

    private static Bounds OffsetBounds(Bounds bounds, ObjectLayoutAxis axis, float offset)
    {
        Vector3 center = bounds.center;
        SetAxisValue(ref center, axis, GetAxisValue(center, axis) + offset);
        bounds.center = center;
        return bounds;
    }

    private static bool TryGetBounds(Transform root, ObjectLayoutSource source, out Bounds bounds)
    {
        bounds = default;
        if (!root.gameObject.activeInHierarchy)
            return false;

        switch (source)
        {
            case ObjectLayoutSource.TransformPosition:
                bounds = new Bounds(root.position, Vector3.zero);
                return true;
            case ObjectLayoutSource.ColliderBounds:
                return TryGetColliderBounds(root, out bounds);
            default:
                if (TryGetRendererBounds(root, out bounds))
                    return true;

                if (TryGetColliderBounds(root, out bounds))
                {
                    Debug.Log($"'{root.name}' has no active Renderer bounds; using Collider bounds fallback.", root);
                    return true;
                }

                return false;
        }
    }

    private static bool TryGetRendererBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        bool found = false;

        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
        foreach (Renderer renderer in renderers)
        {
            if (renderer is ParticleSystemRenderer || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                continue;

            Encapsulate(ref bounds, renderer.bounds, ref found);
        }

        return found;
    }

    private static bool TryGetColliderBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
        bool found = false;

        Collider[] colliders = root.GetComponentsInChildren<Collider>(false);
        foreach (Collider collider in colliders)
        {
            if (!collider.enabled || !collider.gameObject.activeInHierarchy)
                continue;

            Encapsulate(ref bounds, collider.bounds, ref found);
        }

        return found;
    }

    private static string GetSourceDescription(ObjectLayoutSource source)
    {
        switch (source)
        {
            case ObjectLayoutSource.TransformPosition:
                return "Transform position";
            case ObjectLayoutSource.ColliderBounds:
                return "Collider bounds";
            default:
                return "Renderer or Collider bounds";
        }
    }

    private static void Encapsulate(ref Bounds combined, Bounds next, ref bool found)
    {
        if (!found)
        {
            combined = next;
            found = true;
            return;
        }

        combined.Encapsulate(next);
    }

    private readonly struct LayoutItem
    {
        public LayoutItem(Transform transform, Bounds bounds)
        {
            Transform = transform;
            Bounds = bounds;
        }

        public Transform Transform { get; }
        public Bounds Bounds { get; }
    }
}

internal sealed class ObjectLayoutWindow : EditorWindow
{
    private static readonly GUIContent AxisLabel = new GUIContent(
        "Axis",
        "World axis to arrange objects along.");
    private static readonly GUIContent LayoutOrderLabel = new GUIContent(
        "Layout Order",
        "How objects are ordered before layout: current axis position or hierarchy order.");
    private static readonly GUIContent SpacingModeLabel = new GUIContent(
        "Spacing Mode",
        "Pack closes gaps using the Spacing value. Evenly Distribute keeps the first-to-last measured span and spaces objects evenly.");
    private static readonly GUIContent LayoutSourceLabel = new GUIContent(
        "Layout Source",
        "Which measurement to use for layout: renderer bounds, transform position, or collider bounds.");
    private static readonly GUIContent SpacingLabel = new GUIContent(
        "Spacing",
        "Extra world-space distance between adjacent measured items when using Pack mode.");
    private static readonly GUIContent ParentLabel = new GUIContent(
        "Parent",
        "Optional parent whose direct children will be laid out. If empty, the active selected object is used.");
    private static readonly GUIContent UseSelectedAsParentLabel = new GUIContent(
        "Use Selected as Parent",
        "Assign the single selected object as the Parent field.");

    private Transform parentOverride;
    private ObjectLayoutAxis axis = ObjectLayoutAxis.X;
    private ObjectLayoutOrder order = ObjectLayoutOrder.AxisPosition;
    private ObjectSpacingMode spacingMode = ObjectSpacingMode.EvenlyDistribute;
    private ObjectLayoutSource layoutSource = ObjectLayoutSource.RendererBounds;
    private float spacing;

    internal static void ShowWindow()
    {
        ObjectLayoutWindow window = GetWindow<ObjectLayoutWindow>("Object Layout Tool");
        window.minSize = new Vector2(320f, 180f);
        window.Show();
    }

    private void OnSelectionChange()
    {
        Repaint();
    }

    private static void DrawSeparator()
    {
        EditorGUILayout.Space(6f);
        Rect rect = EditorGUILayout.GetControlRect(false, 1f);
        EditorGUI.DrawRect(rect, new Color(0.3f, 0.3f, 0.3f, 1f));
        EditorGUILayout.Space(6f);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Object Layout Tool", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Arrange selected objects or child objects along a chosen axis.", EditorStyles.miniLabel);

        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
        {
            axis = (ObjectLayoutAxis)EditorGUILayout.EnumPopup(AxisLabel, axis);
            order = (ObjectLayoutOrder)EditorGUILayout.EnumPopup(LayoutOrderLabel, order);
            spacingMode = (ObjectSpacingMode)EditorGUILayout.EnumPopup(SpacingModeLabel, spacingMode);
            EditorGUILayout.LabelField("Uniform uses fixed spacing; Bounds accounts for object size.", EditorStyles.miniLabel);

            layoutSource = (ObjectLayoutSource)EditorGUILayout.EnumPopup(LayoutSourceLabel, layoutSource);
            if (spacingMode == ObjectSpacingMode.Pack)
                spacing = EditorGUILayout.FloatField(SpacingLabel, spacing);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Top-Level Selection", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Selected Objects", Selection.transforms.Length.ToString());
            using (new EditorGUI.DisabledScope(!ObjectLayoutTools.CanLayoutSelection()))
            {
                if (GUILayout.Button($"Layout Top-Level Selection On {axis}"))
                    ObjectLayoutTools.LayoutSelection(axis, order, spacingMode, spacing, layoutSource);
            }
            if (!ObjectLayoutTools.CanLayoutSelection())
                EditorGUILayout.LabelField("Select at least two active, measurable objects to lay out the top-level selection.", EditorStyles.miniLabel);

            DrawSeparator();

            parentOverride = (Transform)EditorGUILayout.ObjectField(
                ParentLabel,
                parentOverride,
                typeof(Transform),
                true);

            using (new EditorGUI.DisabledScope(Selection.transforms.Length != 1))
            {
                if (GUILayout.Button(UseSelectedAsParentLabel))
                    parentOverride = Selection.activeTransform;
            }

            Transform parent = parentOverride != null ? parentOverride : Selection.activeTransform;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Layout Children", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!ObjectLayoutTools.CanLayoutParentChildren(parent)))
            {
                if (GUILayout.Button($"Layout Children On {axis}"))
                    ObjectLayoutTools.LayoutParentChildren(parent, axis, order, spacingMode, spacing, layoutSource);
            }
            if (!ObjectLayoutTools.CanLayoutParentChildren(parent))
                EditorGUILayout.LabelField("Select a parent with at least two active, measurable children, or assign one in the Parent field.", EditorStyles.miniLabel);
        }

        if (EditorApplication.isPlayingOrWillChangePlaymode)
            EditorGUILayout.LabelField("Layout tools are disabled in Play Mode.", EditorStyles.miniLabel);
    }
}

