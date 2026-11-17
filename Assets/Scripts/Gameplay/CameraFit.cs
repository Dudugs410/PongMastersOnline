using UnityEngine;

[RequireComponent(typeof(Camera))]
public class CameraFit : MonoBehaviour
{
    public float fieldWidth = 19f;
    public float fieldHeight = 10f;

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
