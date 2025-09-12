using Meta.XR.ImmersiveDebugger.UserInterface.Generic;
using System;
using UnityEngine;

public class AnchorPlacement : MonoBehaviour
{
    public GameObject anchorPrefab;
    void Update()
    {
        if (OVRInput.GetDown(OVRInput. Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
        {
            CreateSpatialAnchor();
        }
    }

    public void CreateSpatialAnchor()
    {
        GameObject prefab = Instantiate(anchorPrefab, OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch), OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch));
    }
}
