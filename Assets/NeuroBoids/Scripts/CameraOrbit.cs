using UnityEngine;

public class CameraOrbit : MonoBehaviour
{
    public Transform target;
    public float distance = 50f;
    public float height = 14f;
    [Tooltip("Graus por segundo. 0 = câmera parada.")]
    public float degreesPerSecond = 6f;

    float angle;

    void Start()
    {
        if (target == null) return;
        Vector3 flat = transform.position - target.position;
        flat.y = 0f;
        if (flat.sqrMagnitude > 0.01f) angle = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
    }

    void LateUpdate()
    {
        if (target == null) return;
        angle += degreesPerSecond * Time.deltaTime;
        Quaternion rot = Quaternion.Euler(0f, angle, 0f);
        transform.position = target.position + rot * new Vector3(0f, height, distance);
        transform.LookAt(target.position);
    }
}
