using UnityEngine;

/// <summary>
/// Esfera que persegue o bando. A cada <see cref="retargetInterval"/> segundos escolhe
/// um alvo (o boid com mais vizinhos, ou o mais próximo) e acelera em direção a ele.
/// Os boids reagem com a regra de fuga (FlockManager.fleeWeight).
/// Não tem collider: é um "fantasma" que só assusta, não empurra.
/// </summary>
public class Predator : MonoBehaviour
{
    public FlockManager flock;
    [Min(0.1f)] public float speed = 8f;
    [Min(0.1f)] public float acceleration = 10f;
    [Min(0.1f)] public float retargetInterval = 2.5f;
    [Tooltip("true: mergulha no miolo do maior grupo. false: persegue o boid mais próximo.")]
    public bool chaseDensestGroup = true;

    Boid target;
    Vector3 velocity;
    float timer;

    void Update()
    {
        if (flock == null || flock.Count == 0) return;
        float dt = Time.deltaTime;

        timer -= dt;
        if (target == null || timer <= 0f)
        {
            target = chaseDensestGroup ? flock.DensestBoid() : flock.NearestBoid(transform.position);
            timer = retargetInterval;
        }
        if (target == null) return;

        Vector3 pos = transform.position;
        Vector3 toTarget = target.Position - pos;

        // não sai muito do mundo do bando
        Vector3 fromCenter = pos - flock.transform.position;
        if (fromCenter.magnitude > flock.boundsRadius * 1.2f) toTarget = -fromCenter;

        Vector3 desired = toTarget.sqrMagnitude > 1e-6f ? toTarget.normalized * speed : velocity;
        velocity = Vector3.MoveTowards(velocity, desired, acceleration * dt);
        if (!float.IsFinite(velocity.x) || !float.IsFinite(velocity.y) || !float.IsFinite(velocity.z))
            velocity = Vector3.zero;

        transform.position = pos + velocity * dt;
        if (velocity.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(velocity);
    }
}
