using UnityEngine;

/// <summary>
/// Um agente do bando (Reynolds, 1987). Não conhece o bando inteiro: só enxerga
/// os vizinhos dentro do raio de percepção e reage a eles com três regras locais
/// (separação, alinhamento, coesão) + limites do mundo, desvio de obstáculos e fuga.
///
/// Todos os parâmetros (pesos, raios, maxSpeed, maxForce) ficam no FlockManager,
/// para que um ajuste no Inspector afete o bando inteiro em tempo real.
///
/// Zero alocação por frame: a percepção usa Physics.OverlapSphereNonAlloc com o
/// mesmo array compartilhado (FlockManager.NeighborBuffer) e o desvio usa
/// Physics.SphereCast com RaycastHit (struct).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(SphereCollider))]
public class Boid : MonoBehaviour
{
    [Tooltip("Desenha o raio de percepção deste boid com Gizmos (Scene e Game view com Gizmos ligado).")]
    public bool drawPerceptionGizmo;

    // Limiares numéricos
    const float MinSpeedForRotation = 0.01f;           // LookRotation só se |v| > 0.01
    const float MinSpeedForRotationSqr = MinSpeedForRotation * MinSpeedForRotation;
    const float MinSeparationDistance = 0.1f;          // evita 1/d² explodir quando d -> 0
    const float CoincidentSqrDistance = 1e-8f;         // vizinho exatamente na mesma posição

    Rigidbody rb;
    Collider ownCollider;
    FlockManager flock;

    Vector3 pendingForce;
    bool fleeing;

    // Dados de depuração (lidos pelos Gizmos e pelo FlockManager)
    int lastNeighborCount;
    Vector3 lastSeparation, lastAlignment, lastCohesion, lastBounds, lastAvoidance, lastFlee;
    bool lastObstacleHit;
    Vector3 lastObstaclePoint, lastReflectDir;

    public Vector3 Position => rb.position;
    public Vector3 Velocity => rb.linearVelocity;
    public int NeighborCount => lastNeighborCount;
    public bool IsFleeing => fleeing;

    // Últimas forças (sem peso) de cada regra: só para depuração/estatística.
    public Vector3 LastSeparation => lastSeparation;
    public Vector3 LastAlignment => lastAlignment;
    public Vector3 LastCohesion => lastCohesion;
    public Vector3 LastBounds => lastBounds;
    public Vector3 LastAvoidance => lastAvoidance;
    public Vector3 LastFlee => lastFlee;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        ownCollider = GetComponent<Collider>();
    }

    /// <summary>Chamado uma vez pelo FlockManager logo após o Instantiate.</summary>
    public void Init(FlockManager manager, Vector3 initialVelocity)
    {
        flock = manager;
        if (rb == null) Awake();
        rb.useGravity = false;
        rb.freezeRotation = true; // a rotação é controlada pela velocidade, não pela física
        rb.linearVelocity = initialVelocity;
        if (initialVelocity.sqrMagnitude > MinSpeedForRotationSqr)
            transform.rotation = Quaternion.LookRotation(initialVelocity);
    }

    // ------------------------------------------------------------------
    // Fase 1 (todos os boids): calcular a força a partir do estado atual.
    // Fase 2 (todos os boids): integrar. As duas fases separadas garantem
    // que todos os boids "enxergam" o mesmo instante do bando.
    // ------------------------------------------------------------------

    public void ComputeStep()
    {
        pendingForce = ComputeForce();
    }

    public void ApplyStep(float dt)
    {
        var s = flock;
        Vector3 force = IsFinite(pendingForce) ? pendingForce : Vector3.zero;

        // 1) limita a força final por maxForce
        force = Vector3.ClampMagnitude(force, s.maxForce);

        // 2) integra (massa unitária: força == aceleração)
        Vector3 v = rb.linearVelocity;
        if (!IsFinite(v)) v = Vector3.zero;
        v += force * dt;

        // 3) limita a velocidade por maxSpeed (em pânico o boid pode correr um pouco mais)
        float speedLimit = s.maxSpeed * (fleeing ? s.panicSpeedMultiplier : 1f);
        v = Vector3.ClampMagnitude(v, speedLimit);

        // velocidade mínima para o bando não "parar no ar"
        float speed = v.magnitude;
        if (speed < s.minSpeed)
        {
            Vector3 dir = speed > MinSpeedForRotation ? v / speed : SafeForward();
            v = dir * s.minSpeed;
        }

        // 4) rede de segurança contra NaN/Infinity: nenhum boid some
        if (!IsFinite(v)) v = SafeForward() * s.minSpeed;
        if (!IsFinite(rb.position))
        {
            rb.position = s.transform.position + Random.insideUnitSphere * s.spawnRadius;
            v = Random.onUnitSphere * s.minSpeed;
        }

        rb.linearVelocity = v;

        // 5) LookRotation só quando |v| > 0.01 (evita "Look rotation viewing vector is zero")
        if (v.sqrMagnitude > MinSpeedForRotationSqr)
        {
            Quaternion target = Quaternion.LookRotation(v);
            Quaternion current = rb.rotation;
            if (!IsFinite(current)) current = target;
            rb.MoveRotation(Quaternion.Slerp(current, target, Mathf.Clamp01(s.turnSmoothing * dt)));
        }
    }

    /// <summary>Soma ponderada de todas as forças de steering (Reynolds).</summary>
    public Vector3 ComputeForce()
    {
        var s = flock;
        Vector3 pos = rb.position;
        Vector3 vel = rb.linearVelocity;
        if (!IsFinite(vel)) vel = Vector3.zero;

        // ---------------- Percepção (sem alocação) ----------------
        Collider[] buffer = s.NeighborBuffer;
        int hits = Physics.OverlapSphereNonAlloc(pos, s.perceptionRadius, buffer, s.boidMask,
                                                 QueryTriggerInteraction.Ignore);

        Vector3 separationSum = Vector3.zero;
        Vector3 velocitySum = Vector3.zero;
        Vector3 positionSum = Vector3.zero;
        int neighbors = 0;
        float perceptionSqr = s.perceptionRadius * s.perceptionRadius;
        float separationSqr = s.separationRadius * s.separationRadius;
        float minSepSqr = MinSeparationDistance * MinSeparationDistance;

        for (int i = 0; i < hits; i++)
        {
            Collider c = buffer[i];
            if (c == ownCollider) continue;
            Rigidbody other = c.attachedRigidbody;
            if (other == null) continue;

            Vector3 otherPos = other.position;
            Vector3 offset = pos - otherPos;            // aponta do vizinho para mim
            float d2 = offset.sqrMagnitude;
            if (d2 > perceptionSqr || d2 < CoincidentSqrDistance) continue;

            neighbors++;
            velocitySum += other.linearVelocity;
            positionSum += otherPos;

            // Separação: direção para longe do vizinho, intensidade ∝ 1/d²
            if (d2 < separationSqr)
            {
                float d = Mathf.Sqrt(d2);
                separationSum += (offset / d) / Mathf.Max(d2, minSepSqr);
            }
        }
        lastNeighborCount = neighbors;

        Vector3 separation = Vector3.zero, alignment = Vector3.zero, cohesion = Vector3.zero;
        if (neighbors > 0)
        {
            // Separação: 1 vizinho a 1 m gera um empurrão de módulo maxForce;
            // a 0,5 m, 4× maxForce (antes do clamp final).
            separation = separationSum * s.maxForce;

            // Alinhamento: voar na direção média das velocidades dos vizinhos
            Vector3 avgVelocity = velocitySum / neighbors;
            alignment = Steer(avgVelocity, vel, s);

            // Coesão: acelerar em direção ao centro de massa local, proporcional à
            // distância até ele (mola). Não usa "desejada − atual" de propósito: o termo
            // −velocidade frearia o bando e o deixaria preso abaixo de maxSpeed.
            Vector3 center = positionSum / neighbors;
            cohesion = Vector3.ClampMagnitude((center - pos) * (s.maxForce / s.perceptionRadius), s.maxForce);
        }

        // ---------------- Limites do mundo ----------------
        // Zero dentro do raio; fora dele, mola linear de volta ao centro.
        Vector3 bounds = Vector3.zero;
        Vector3 fromCenter = pos - s.transform.position;
        float distCenter = fromCenter.magnitude;
        if (distCenter > s.boundsRadius && distCenter > 0f)
        {
            float excess = distCenter - s.boundsRadius;
            bounds = -(fromCenter / distCenter) * (excess * s.boundsStrength);
        }

        // ---------------- Desvio de obstáculos ----------------
        Vector3 avoidance = Vector3.zero;
        lastObstacleHit = false;
        float speed = vel.magnitude;
        if (speed > MinSpeedForRotation)
        {
            Vector3 dir = vel / speed;
            float lookAhead = s.avoidLookAhead + speed * s.avoidLookAheadPerSpeed;
            if (Physics.SphereCast(pos, s.avoidCastRadius, dir, out RaycastHit hit, lookAhead,
                                   s.obstacleMask, QueryTriggerInteraction.Ignore))
            {
                // Rebate a direção de voo na superfície; o termo de normal garante
                // que mesmo um ângulo rasante aponte para longe da parede.
                Vector3 reflected = Vector3.Reflect(dir, hit.normal);
                Vector3 desired = reflected + hit.normal * 0.5f;
                float urgency = 1f - Mathf.Clamp01(hit.distance / lookAhead); // 0 longe, 1 colado
                avoidance = Steer(desired, vel, s) * Mathf.Lerp(0.25f, 3f, urgency);

                lastObstacleHit = true;
                lastObstaclePoint = hit.point;
                lastReflectDir = reflected;
            }
        }

        // ---------------- Fuga do predador ----------------
        Vector3 flee = Vector3.zero;
        fleeing = false;
        Transform predator = s.predator;
        if (predator != null && predator.gameObject.activeInHierarchy)
        {
            Vector3 away = pos - predator.position;
            float d2 = away.sqrMagnitude;
            if (d2 < s.fleeRadius * s.fleeRadius)
            {
                fleeing = true;
                float d = Mathf.Sqrt(d2);
                float proximity = 1f - d / s.fleeRadius; // 0 na borda, 1 encostado
                flee = Steer(away, vel, s) * (0.5f + proximity);
            }
        }

        lastSeparation = separation; lastAlignment = alignment; lastCohesion = cohesion;
        lastBounds = bounds; lastAvoidance = avoidance; lastFlee = flee;

        Vector3 total = s.separationWeight * separation
                      + s.alignmentWeight * alignment
                      + s.cohesionWeight * cohesion
                      + s.boundsWeight * bounds
                      + s.avoidanceWeight * avoidance
                      + s.fleeWeight * flee;

        return IsFinite(total) ? total : Vector3.zero;
    }

    /// <summary>Steering de Reynolds: (velocidade desejada − velocidade atual), limitado por maxForce.</summary>
    static Vector3 Steer(Vector3 desiredDirection, Vector3 currentVelocity, FlockManager s)
    {
        float m = desiredDirection.magnitude;
        if (m < 1e-5f || float.IsNaN(m)) return Vector3.zero;
        Vector3 desired = desiredDirection * (s.maxSpeed / m);
        return Vector3.ClampMagnitude(desired - currentVelocity, s.maxForce);
    }

    Vector3 SafeForward()
    {
        Vector3 f = transform.forward;
        return IsFinite(f) && f.sqrMagnitude > 0.5f ? f : Vector3.forward;
    }

    static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    static bool IsFinite(Quaternion q) =>
        float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w) &&
        (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w) > 1e-6f;

    // ------------------------------------------------------------------
    // Gizmos: raio de percepção (amarelo), raio de separação (vermelho),
    // linhas até os vizinhos (verde), velocidade (ciano) e SphereCast/Reflect.
    // ------------------------------------------------------------------

    void OnDrawGizmos()
    {
        if (drawPerceptionGizmo) DrawPerceptionGizmo();
    }

    void OnDrawGizmosSelected()
    {
        if (!drawPerceptionGizmo) DrawPerceptionGizmo();
    }

    void DrawPerceptionGizmo()
    {
        FlockManager s = flock != null ? flock : FlockManagerInScene();
        if (s == null) return;
        Vector3 pos = transform.position;

        Gizmos.color = new Color(1f, 0.9f, 0.1f, 0.9f);
        Gizmos.DrawWireSphere(pos, s.perceptionRadius);
        Gizmos.color = new Color(1f, 0.25f, 0.2f, 0.9f);
        Gizmos.DrawWireSphere(pos, s.separationRadius);

        if (!Application.isPlaying || rb == null) return;

        // Vizinhos atuais (só no editor; reutiliza o mesmo buffer)
        Collider[] buffer = s.NeighborBuffer;
        if (buffer != null)
        {
            int hits = Physics.OverlapSphereNonAlloc(pos, s.perceptionRadius, buffer, s.boidMask,
                                                     QueryTriggerInteraction.Ignore);
            Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.6f);
            for (int i = 0; i < hits; i++)
                if (buffer[i] != ownCollider) Gizmos.DrawLine(pos, buffer[i].transform.position);
        }

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(pos, pos + rb.linearVelocity * 0.5f);

        if (lastObstacleHit)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(pos, lastObstaclePoint);
            Gizmos.DrawWireSphere(lastObstaclePoint, s.avoidCastRadius);
            Gizmos.DrawLine(lastObstaclePoint, lastObstaclePoint + lastReflectDir * 2f);
        }
    }

    FlockManager FlockManagerInScene()
    {
        // Só usado no modo de edição para o Gizmo de um boid solto na cena.
        return transform.parent != null ? transform.parent.GetComponent<FlockManager>() : null;
    }
}
