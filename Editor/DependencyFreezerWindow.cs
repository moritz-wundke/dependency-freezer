#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DependencyFreezer.Editor;

public sealed class DependencyFreezerWindow : EditorWindow
{
    private readonly Dictionary<string, bool> _selection = new(StringComparer.Ordinal);
    private Vector2 _scrollPosition;
    private string _status = "Ready";
    private List<PackageStatusSnapshot> _packages = new();

    [MenuItem("Tools/Dependency Freezer")]
    public static void Open() => GetWindow<DependencyFreezerWindow>("Dependency Freezer");

    private void OnEnable() => RefreshPackages();

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Dependency Freezer", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Freeze registry-hosted UPM packages into embedded packages tracked in frozen-lock.json.", MessageType.Info);
        EditorGUILayout.LabelField("Project", GetProjectRoot());
        EditorGUILayout.LabelField("Status", _status);
        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Refresh"))
            {
                RefreshPackages();
            }

            if (GUILayout.Button("Freeze Selected"))
            {
                RunOperation(FreezeSelected);
            }

            if (GUILayout.Button("Freeze All Eligible"))
            {
                RunOperation(FreezeAll);
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Unfreeze Selected Tree"))
            {
                RunOperation(UnfreezeSelected);
            }

            if (GUILayout.Button("Unfreeze All"))
            {
                RunOperation(UnfreezeAll);
            }

            if (GUILayout.Button("Validate"))
            {
                RunOperation(ValidateProject);
            }
        }

        EditorGUILayout.Space();
        _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
        foreach (var package in _packages.OrderBy(package => package.Name, StringComparer.Ordinal))
        {
            var selected = _selection.TryGetValue(package.Name, out var isSelected) && isSelected;
            _selection[package.Name] = EditorGUILayout.ToggleLeft($"{package.Name} [{package.State}]", selected);
            EditorGUILayout.LabelField("Current", package.CurrentReference ?? "<none>");
            if (!string.IsNullOrWhiteSpace(package.OriginalReference))
            {
                EditorGUILayout.LabelField("Original", package.OriginalReference);
            }

            if (package.Dependencies.Count > 0)
            {
                EditorGUILayout.LabelField("Dependencies", string.Join(", ", package.Dependencies));
            }

            EditorGUILayout.Space();
        }

        EditorGUILayout.EndScrollView();
    }

    private void RefreshPackages()
    {
        try
        {
            using var engine = new DependencyFreezerEngine();
            _packages = engine.InspectAsync(GetProjectRoot()).GetAwaiter().GetResult().ToList();
            foreach (var package in _packages)
            {
                _selection.TryAdd(package.Name, false);
            }

            _status = $"Loaded {_packages.Count} packages";
        }
        catch (Exception exception)
        {
            _status = exception.Message;
        }
    }

    private void FreezeSelected()
    {
        using var engine = new DependencyFreezerEngine();
        var selectedPackages = _selection.Where(pair => pair.Value).Select(pair => pair.Key).ToArray();
        engine.FreezeAsync(new FreezeRequest(GetProjectRoot(), selectedPackages.Length == 0 ? null : selectedPackages)).GetAwaiter().GetResult();
        AssetDatabase.Refresh();
        RefreshPackages();
    }

    private void FreezeAll()
    {
        using var engine = new DependencyFreezerEngine();
        engine.FreezeAsync(new FreezeRequest(GetProjectRoot())).GetAwaiter().GetResult();
        AssetDatabase.Refresh();
        RefreshPackages();
    }

    private void UnfreezeSelected()
    {
        using var engine = new DependencyFreezerEngine();
        var selectedPackages = _selection.Where(pair => pair.Value).Select(pair => pair.Key).ToArray();
        var preview = engine.PreviewUnfreezeAsync(new UnfreezeRequest(GetProjectRoot(), selectedPackages, false)).GetAwaiter().GetResult();
        if (!preview.CanProceed)
        {
            throw new InvalidOperationException($"Selected packages cannot be unfrozen safely because these packages are still shared: {string.Join(", ", preview.BlockingPackages)}.");
        }

        if (EditorUtility.DisplayDialog("Unfreeze packages", $"This will unfreeze: {string.Join(", ", preview.ImpactedPackages)}", "Continue", "Cancel"))
        {
            engine.UnfreezeAsync(new UnfreezeRequest(GetProjectRoot(), selectedPackages, false)).GetAwaiter().GetResult();
            AssetDatabase.Refresh();
            RefreshPackages();
        }
    }

    private void UnfreezeAll()
    {
        using var engine = new DependencyFreezerEngine();
        if (EditorUtility.DisplayDialog("Unfreeze all packages", "This will unfreeze every frozen dependency.", "Continue", "Cancel"))
        {
            engine.UnfreezeAsync(new UnfreezeRequest(GetProjectRoot(), null, true)).GetAwaiter().GetResult();
            AssetDatabase.Refresh();
            RefreshPackages();
        }
    }

    private void ValidateProject()
    {
        using var engine = new DependencyFreezerEngine();
        var validation = engine.ValidateAsync(GetProjectRoot()).GetAwaiter().GetResult();
        _status = validation.Success
            ? "Validation succeeded"
            : string.Join(" | ", validation.Issues.Select(issue => issue.Message));
        RefreshPackages();
    }

    private void RunOperation(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _status = exception.Message;
        }
    }

    private static string GetProjectRoot() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
}
#endif
