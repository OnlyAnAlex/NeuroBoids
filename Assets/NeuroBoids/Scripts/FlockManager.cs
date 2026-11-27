using UnityEngine;

/// <summary>
/// Instancia o bando e guarda TODOS os parâmetros ajustáveis no Inspector.
/// Não há líder nem coreografia: o FlockManager só cria os boids e avança a
/// simulação em duas fases (calcular forças → integrar) a cada passo de física.
/// </summary>
[DefaultExecutionOrder(-50)]
public class FlockManager : MonoBehaviour
{
    [Header("Bando")]
    public Boid boidPrefab;
    [Min(1)] public int boidCount = 200;
    [Min(0.1f)] public float spawnRadius = 10f;
    [Tooltip("Índice do boid que desenha o raio de percepção com OnDrawGizmos.")]
    public int gizmoBoidIndex = 0;

    [Header("Pesos das regras (somados no ComputeForce)")]
    [Range(0f, 10f)] public float separationWeight = 1.5f;
    [Range(0f, 10f)] public float alignmentWeight = 1.5f;
    [Range(0f, 10f)] public float cohesionWeight = 0.8f;
    [Range(0f, 10f)] public float boundsWeight = 1.0f;
    [Range(0f, 20f)] public float avoidanceWeight = 6.0f;
    [Range(0f, 50f)] public float fleeWeight = 12.0f;

    [Header("Percepção")]
    [Min(0.05f)] public float perceptionRadius = 3.0f;
    [Min(0.05f)] public float separationRadius = 2.0f;
    [Tooltip("Tamanho do array reutilizado por OverlapSphereNonAlloc.")]
    [Min(4)] public int maxNeighbors = 64;
    public LayerMask boidMask;

    [Header("Movimento")]
    [Min(0.1f)] public float maxSpeed = 6f;
    [Min(0f)] public float minSpeed = 2f;
    [Min(0.1f)] public float maxForce = 8f;
    [Tooltip("Velocidade de giro do modelo em direção ao vetor velocidade.")]
    [Min(0.1f)] public float turnSmoothing = 10f;

    [Header("Limites do mundo")]
    [Tooltip("Dentro deste raio (a partir do FlockManager) a força de retorno é zero.")]
    [Min(1f)] public float boundsRadius = 22f;
    [Tooltip("Força por metro fora do raio.")]
    [Min(0f)] public float boundsStrength = 6f;

    [Header("Obstáculos (SphereCast + Vector3.Reflect)")]
    public LayerMask obstacleMask;
    [Min(0.1f)] public float avoidLookAhead = 3f;
    [Min(0f)] public float avoidLookAheadPerSpeed = 0.35f;
    [Min(0.01f)] public float avoidCastRadius = 0.5f;

    [Header("Predador (opcional)")]
    [Tooltip("Ative o GameObject do predador para o teste de fuga.")]
    public Transform predator;
    [Min(0.1f)] public float fleeRadius = 7f;
    [Min(1f)] public float panicSpeedMultiplier = 1.5f;

    [Header("Depuração")]
    [Tooltip("> 0 imprime estatísticas do bando no Console a cada N segundos (gera strings: use só em testes).")]
    [Min(0f)] public float logStatsEvery = 0f;
    [Tooltip("Distância usada só nas estatísticas para decidir se dois boids estão no mesmo grupo.")]
    [Min(0.1f)] public float statsGroupLinkDistance = 3f;

    /// <summary>Array único reutilizado por todos os boids na percepção.</summary>
    public Collider[] NeighborBuffer { get; private set; }
    public Boid[] Boids => boids;
    public int Count => spawned;

    Boid[] boids;
    int spawned;
    int[] unionParent; // buffer pré-alocado para contar grupos
    float statsTimer;

    void Awake()
    {
        NeighborBuffer = new Collider[maxNeighbors];
        int boidLayer = LayerMask.NameToLayer("Boid");
        if (boidLayer >= 0)
        {
            if (boidMask.value == 0) boidMask = 1 << boidLayer;
            // Boids não colidem fisicamente entre si: quem os separa é a regra de separação.
            Physics.IgnoreLayerCollision(boidLayer, boidLayer, true);
        }
        else
        {
            Debug.LogError("FlockManager: crie a camada 'Boid' (Project Settings > Tags and Layers).");
        }
    }

    void Start()
    {
        if (boidPrefab == null) { Debug.LogError("FlockManager: boidPrefab não definido."); enabled = false; return; }

        boids = new Boid[boidCount];
        unionParent = new int[boidCount];
        int boidLayer = LayerMask.NameToLayer("Boid");
        Vector3 center = transform.position;

        for (int i = 0; i < boidCount; i++)
        {
            // não nasce dentro de um obstáculo
            Vector3 p = center + Random.insideUnitSphere * spawnRadius;
            for (int tries = 0; tries < 30 && Physics.CheckSphere(p, 1f, obstacleMask, QueryTriggerInteraction.Ignore); tries++)
                p = center + Random.insideUnitSphere * spawnRadius;
            Quaternion r = Random.rotation;
            Boid b = Instantiate(boidPrefab, p, r, transform);
            b.name = "Boid_" + i.ToString("000");
            if (boidLayer >= 0) b.gameObject.layer = boidLayer;
            b.Init(this, (r * Vector3.forward) * Random.Range(minSpeed, maxSpeed));
            b.drawPerceptionGizmo = i == gizmoBoidIndex;
            boids[i] = b;
        }
        spawned = boidCount;
    }

    void FixedUpdate()
    {
        if (boids == null) return;
        if (NeighborBuffer.Length != maxNeighbors) NeighborBuffer = new Collider[maxNeighbors]; // só se mudou no Inspector

        // Fase 1: todos calculam a força com o mesmo "retrato" do bando.
        for (int i = 0; i < spawned; i++) boids[i].ComputeStep();
        // Fase 2: todos integram.
        float dt = Time.fixedDeltaTime;
        for (int i = 0; i < spawned; i++) boids[i].ApplyStep(dt);

        if (logStatsEvery > 0f)
        {
            statsTimer -= dt;
            if (statsTimer <= 0f) { statsTimer = logStatsEvery; LogStats(); }
        }
    }

    void OnValidate()
    {
        if (separationRadius > perceptionRadius) separationRadius = perceptionRadius;
        if (minSpeed > maxSpeed) minSpeed = maxSpeed;
        if (boids != null)
            for (int i = 0; i < spawned; i++)
                if (boids[i] != null) boids[i].drawPerceptionGizmo = i == gizmoBoidIndex;
    }

    /// <summary>Boid com mais vizinhos = miolo do maior grupo (alvo do predador).</summary>
    public Boid DensestBoid()
    {
        Boid best = null;
        int bestCount = -1;
        for (int i = 0; i < spawned; i++)
            if (boids[i].NeighborCount > bestCount) { bestCount = boids[i].NeighborCount; best = boids[i]; }
        return best;
    }

    /// <summary>Boid mais próximo de um ponto.</summary>
    public Boid NearestBoid(Vector3 point)
    {
        Boid best = null;
        float bestSqr = float.MaxValue;
        for (int i = 0; i < spawned; i++)
        {
            float d = (boids[i].Position - point).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = boids[i]; }
        }
        return best;
    }

    // ------------------------------------------------------------------
    // Estatísticas para os testes (sob demanda; não roda por frame).
    // ------------------------------------------------------------------

    public struct FlockStats
    {
        public int count;
        public float polarization;          // |média das direções| : 0 = caótico, 1 = todos alinhados
        public float groupPolarization;     // mesma medida, mas dentro de cada grupo (média ponderada)
        public float meanNearestNeighbor;   // distância média ao vizinho mais próximo
        public float minNearestNeighbor;
        public float meanDistanceToCentroid;// "raio" do bando
        public int groups;                  // componentes conexos com ligação = perceptionRadius
        public int largestGroup;
        public float meanSpeed;
        public float meanNeighbors;
        public int fleeing;
        public int outsideBounds;
        public int insideObstacle;          // deve ser sempre 0
        public int invalid;                 // posição/velocidade NaN: deve ser sempre 0

        public override string ToString() =>
            $"n={count} polar={polarization:F2} polarGrupo={groupPolarization:F2} nnMed={meanNearestNeighbor:F2} nnMin={minNearestNeighbor:F2} " +
            $"raio={meanDistanceToCentroid:F1} grupos={groups} maior={largestGroup} vel={meanSpeed:F2} " +
            $"vizinhos={meanNeighbors:F1} fugindo={fleeing} foraLimite={outsideBounds} " +
            $"dentroObstaculo={insideObstacle} NaN={invalid}";
    }

    public FlockStats ComputeStats()
    {
        var st = new FlockStats { count = spawned };
        if (spawned == 0) return st;

        Vector3 centroid = Vector3.zero, dirSum = Vector3.zero;
        float speedSum = 0f, neighSum = 0f;
        for (int i = 0; i < spawned; i++)
        {
            Boid b = boids[i];
            centroid += b.Position;
            Vector3 v = b.Velocity;
            float sp = v.magnitude;
            speedSum += sp;
            if (sp > 1e-4f) dirSum += v / sp;
            neighSum += b.NeighborCount;
            if (b.IsFleeing) st.fleeing++;
            if ((b.Position - transform.position).magnitude > boundsRadius) st.outsideBounds++;
            if (Physics.CheckSphere(b.Position, 0.05f, obstacleMask, QueryTriggerInteraction.Ignore)) st.insideObstacle++;
            if (float.IsNaN(b.Position.x + b.Position.y + b.Position.z + v.x + v.y + v.z)) st.invalid++;
            unionParent[i] = i;
        }
        centroid /= spawned;
        st.polarization = dirSum.magnitude / spawned;
        st.meanSpeed = speedSum / spawned;
        st.meanNeighbors = neighSum / spawned;

        float nnSum = 0f, nnMin = float.MaxValue, cSum = 0f;
        float linkSqr = statsGroupLinkDistance * statsGroupLinkDistance;
        for (int i = 0; i < spawned; i++)
        {
            Vector3 pi = boids[i].Position;
            cSum += (pi - centroid).magnitude;
            float best = float.MaxValue;
            for (int j = 0; j < spawned; j++)
            {
                if (i == j) continue;
                float d = (pi - boids[j].Position).sqrMagnitude;
                if (d < best) best = d;
                if (j > i && d < linkSqr) Union(i, j);
            }
            best = Mathf.Sqrt(best);
            nnSum += best;
            if (best < nnMin) nnMin = best;
        }
        st.meanNearestNeighbor = nnSum / spawned;
        st.minNearestNeighbor = nnMin;
        st.meanDistanceToCentroid = cSum / spawned;

        // conta grupos, o tamanho do maior e a polarização dentro de cada grupo
        int[] size = new int[spawned];           // alocações só nesta chamada de depuração
        Vector3[] groupDir = new Vector3[spawned];
        for (int i = 0; i < spawned; i++)
        {
            int root = Find(i);
            size[root]++;
            Vector3 v = boids[i].Velocity;
            float sp = v.magnitude;
            if (sp > 1e-4f) groupDir[root] += v / sp;
        }
        float gp = 0f;
        for (int i = 0; i < spawned; i++)
        {
            if (size[i] == 0) continue;
            st.groups++;
            if (size[i] > st.largestGroup) st.largestGroup = size[i];
            gp += groupDir[i].magnitude; // = |média| * tamanho do grupo
        }
        st.groupPolarization = gp / spawned;
        return st;
    }

    int Find(int i)
    {
        while (unionParent[i] != i) { unionParent[i] = unionParent[unionParent[i]]; i = unionParent[i]; }
        return i;
    }

    void Union(int a, int b)
    {
        a = Find(a); b = Find(b);
        if (a != b) unionParent[a] = b;
    }

    [ContextMenu("Log Flock Stats")]
    public void LogStats()
    {
        if (boids == null) { Debug.Log("FlockManager: entre no modo Play para ver as estatísticas."); return; }
        Debug.Log("[Flock] " + ComputeStats());
    }

    void OnDrawGizmos()
    {
        // Limite do mundo (a força de retorno é zero dentro desta esfera)
        Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, boundsRadius);
        if (predator != null && predator.gameObject.activeInHierarchy)
        {
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.5f);
            Gizmos.DrawWireSphere(predator.position, fleeRadius);
        }
    }
}
