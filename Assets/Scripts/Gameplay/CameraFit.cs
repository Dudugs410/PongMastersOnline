using UnityEngine;

// Keeps the whole field visible on any screen shape by zooming the orthographic camera out
// when the window is narrower than the field.
[RequireComponent(typeof(Camera))]
public class CameraFit : MonoBehaviour
{
    public float fieldWidth = 19f;    // world units that must always fit horizontally
    public float fieldHeight = 10f;   // world units that must always fit vertically

    Camera cam;

    void Awake()
    {
        cam = GetComponent<Camera>();
    }

    void LateUpdate()
    {
        float sizeForHeight = fieldHeight * 0.5f;
        float sizeForWidth = fieldWidth * 0.5f / cam.aspect;
        cam.orthographicSize = Mathf.Max(sizeForHeight, sizeForWidth);
    }
}
