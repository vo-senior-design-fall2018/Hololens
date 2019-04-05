using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.WSA.Input;

public class TapHandler : MonoBehaviour
{
    public UnityEvent tapEventCallback;
    GestureRecognizer gestureRecognizer;

    void Awake()
    {
        gestureRecognizer = new GestureRecognizer();
        gestureRecognizer.SetRecognizableGestures(GestureSettings.Tap);
        gestureRecognizer.StartCapturingGestures();
    }

    void OnEnable()
    {
        gestureRecognizer.Tapped += Tap;
    }

    void OnDisable()
    {
        gestureRecognizer.Tapped -= Tap;
    }

    private void Tap(TappedEventArgs eventArgs)
    {
        tapEventCallback.Invoke();
        Debug.Log("Tapped");
    }
}
