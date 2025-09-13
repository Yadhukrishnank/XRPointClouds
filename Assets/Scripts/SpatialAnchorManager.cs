using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

public class SpatialAnchorManager : MonoBehaviour
{
    public OVRSpatialAnchor anchorPrefab;
    public const string NumUuidsplayerPref = "NumUuids";

    private Canvas canvas;
    private TextMeshProUGUI uuidText;
    private TextMeshProUGUI savedStatusText;

    private readonly List<OVRSpatialAnchor> anchors = new();
    private OVRSpatialAnchor lastCreatedAnchor;
    private AnchorLoader anchorLoader;

    private void Awake()
    {
        anchorLoader = GetComponent<AnchorLoader>();
        if (anchorLoader == null)
            Debug.LogWarning("AnchorLoader not found on the same GameObject. Loading will be unavailable.");
    }

    private void Update()
    {
        if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
            CreateSpatialAnchor();

        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
            SaveLastCreatedAnchor();          // async fire-and-forget

        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch))
            UnsaveLastCreatedAnchor();        // async fire-and-forget

        if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch))
            UnsaveAllAnchors();               // async fire-and-forget

        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch))
            LoadSavedAnchors();
    }

    public void CreateSpatialAnchor()
    {
        var pos = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
        var rot = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
        var workingAnchor = Instantiate(anchorPrefab, pos, rot);

        canvas = workingAnchor.gameObject.GetComponentInChildren<Canvas>(true);
        if (canvas != null)
        {
            uuidText = canvas.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
            savedStatusText = canvas.transform.GetChild(1).GetComponent<TextMeshProUGUI>();
        }
        else
        {
            uuidText = null;
            savedStatusText = null;
        }

        StartCoroutine(AnchorCreated(workingAnchor));
    }

    private IEnumerator AnchorCreated(OVRSpatialAnchor workingAnchor)
    {
        while (!workingAnchor.Created)
            yield return null;

        Guid anchorGuid = workingAnchor.Uuid;
        anchors.Add(workingAnchor);
        lastCreatedAnchor = workingAnchor;

        if (uuidText != null) uuidText.text = "UUID: " + anchorGuid.ToString();
        if (savedStatusText != null) savedStatusText.text = "Not Saved";
    }

    private async void SaveLastCreatedAnchor()
    {
        if (lastCreatedAnchor == null)
        {
            Debug.LogWarning("No anchor to save. Create one first.");
            return;
        }

        bool success = await lastCreatedAnchor.SaveAnchorAsync();   // NEW NAME
        if (savedStatusText != null)
            savedStatusText.text = success ? "Saved" : "Save Failed";

        if (success)
            SaveUuidToPlayerPrefs(lastCreatedAnchor.Uuid);
    }

    private void SaveUuidToPlayerPrefs(Guid uuid)
    {
        if (!PlayerPrefs.HasKey(NumUuidsplayerPref))
            PlayerPrefs.SetInt(NumUuidsplayerPref, 0);

        int n = PlayerPrefs.GetInt(NumUuidsplayerPref);
        PlayerPrefs.SetString("Uuid" + n, uuid.ToString());
        PlayerPrefs.SetInt(NumUuidsplayerPref, ++n);
        PlayerPrefs.Save();
    }

    private async void UnsaveLastCreatedAnchor()
    {
        if (lastCreatedAnchor == null)
        {
            Debug.LogWarning("No anchor to erase. Create one first.");
            return;
        }

        bool success = await lastCreatedAnchor.EraseAnchorAsync();  // NEW NAME
        if (success)
        {
            if (savedStatusText != null) savedStatusText.text = "Not Saved";
        }
        else
        {
            Debug.LogWarning("Erase failed for lastCreatedAnchor.");
        }
    }

    private async void UnsaveAllAnchors()
    {
        foreach (var anchor in anchors)
            await UnsaveAnchorAsync(anchor);
    }

    private async Task UnsaveAnchorAsync(OVRSpatialAnchor anchor)
    {
        bool success = await anchor.EraseAnchorAsync();             // NEW NAME
        if (!success) return;

        var texts = anchor.GetComponentsInChildren<TextMeshProUGUI>(true);
        if (texts != null && texts.Length > 1)
            texts[1].text = "Not Saved";
    }

    private void ClearAllUuidsFromPlayerPrefs()
    {
        if (!PlayerPrefs.HasKey(NumUuidsplayerPref)) return;

        int n = PlayerPrefs.GetInt(NumUuidsplayerPref);
        for (int i = 0; i < n; i++)
            PlayerPrefs.DeleteKey("Uuid" + i);

        PlayerPrefs.DeleteKey(NumUuidsplayerPref);
        PlayerPrefs.Save();
    }

    public void LoadSavedAnchors()
    {
        if (anchorLoader == null)
        {
            Debug.LogWarning("AnchorLoader missing. Add it to the same GameObject.");
            return;
        }
        anchorLoader.LoadAnchorsByUuid();
    }
}
