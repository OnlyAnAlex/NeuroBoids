using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// Gera (ou regenera) camadas, materiais, o prefab Boid e a cena NeuroBoids.
/// Menu: NeuroBoids > Rebuild Scene.
/// </summary>
public static class NeuroBoidsSetup
{
    const string Root = "Assets/NeuroBoids";
    const string MatDir = Root + "/Materials";
    const string PrefabDir = Root + "/Prefabs";
    const string PrefabPath = PrefabDir + "/Boid.prefab";
    public const string ScenePath = "Assets/Scenes/NeuroBoids.unity";

    [MenuItem("NeuroBoids/Rebuild Scene")]
    public static void Build()
    {
        int boidLayer = EnsureLayer("Boid", 6);
        int obstacleLayer = EnsureLayer("Obstacle", 7);
        int predatorLayer = EnsureLayer("Predator", 8);
        Physics.IgnoreLayerCollision(boidLayer, boidLayer, true);
        Physics.IgnoreLayerCollision(boidLayer, predatorLayer, true);

        EnsureFolder(MatDir);
        EnsureFolder(PrefabDir);

        Material boidMat = MakeMaterial("Boid", new Color(1f, 0.62f, 0.15f), 0.35f);
        Material headMat = MakeMaterial("BoidHead", new Color(0.15f, 0.2f, 0.3f), 0.5f);
        Material obstacleMat = MakeMaterial("Obstacle", new Color(0.55f, 0.6f, 0.68f), 0.2f);
        Material predatorMat = MakeMaterial("Predator", new Color(0.9f, 0.08f, 0.12f), 0.6f);
        predatorMat.EnableKeyword("_EMISSION");
        predatorMat.SetColor("_EmissionColor", new Color(0.8f, 0.05f, 0.05f) * 1.5f);

        Boid prefab = BuildBoidPrefab(boidLayer, boidMat, headMat);

        // ---------------- Cena ----------------
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        GameObject managerGo = new GameObject("FlockManager");
        FlockManager flock = managerGo.AddComponent<FlockManager>();
        flock.boidPrefab = prefab;
        flock.boidMask = 1 << boidLayer;
        flock.obstacleMask = 1 << obstacleLayer;

        MakeObstacle(PrimitiveType.Sphere, "Obstacle_Sphere", new Vector3(-8f, 2f, 2f), Vector3.one * 9f,
                     Quaternion.identity, obstacleLayer, obstacleMat);
        MakeObstacle(PrimitiveType.Cylinder, "Obstacle_Pillar", new Vector3(9f, 0f, -3f), new Vector3(4f, 14f, 4f),
                     Quaternion.identity, obstacleLayer, obstacleMat);
        MakeObstacle(PrimitiveType.Cube, "Obstacle_Box", new Vector3(3f, -9f, 9f), Vector3.one * 7f,
                     Quaternion.Euler(20f, 35f, 10f), obstacleLayer, obstacleMat);

        GameObject predatorGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        predatorGo.name = "Predator";
        Object.DestroyImmediate(predatorGo.GetComponent<Collider>());
        predatorGo.layer = predatorLayer;
        predatorGo.transform.position = new Vector3(0f, 0f, 30f);
        predatorGo.transform.localScale = Vector3.one * 1.6f;
        predatorGo.GetComponent<MeshRenderer>().sharedMaterial = predatorMat;
        predatorGo.AddComponent<Predator>().flock = flock;
        predatorGo.SetActive(false); // ative para o teste do predador
        flock.predator = predatorGo.transform;

        Camera cam = Camera.main;
        if (cam != null)
        {
            cam.transform.position = new Vector3(0f, 14f, -50f);
            cam.transform.LookAt(Vector3.zero);
            cam.farClipPlane = 300f;
            CameraOrbit orbit = cam.gameObject.AddComponent<CameraOrbit>();
            orbit.target = managerGo.transform;
            orbit.distance = 50f;
            orbit.height = 14f;
            orbit.degreesPerSecond = 6f;
        }

        Light sun = Object.FindFirstObjectByType<Light>();
        if (sun != null) sun.transform.rotation = Quaternion.Euler(45f, -30f, 0f);

        EditorSceneManager.SaveScene(scene, ScenePath);
        AddSceneToBuildSettings(ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("NeuroBoids: cena gerada em " + ScenePath);
    }

    static Boid BuildBoidPrefab(int layer, Material bodyMat, Material headMat)
    {
        GameObject root = new GameObject("Boid");
        root.layer = layer;

        Rigidbody rb = root.AddComponent<Rigidbody>();
        rb.mass = 1f;
        rb.useGravity = false;
        rb.linearDamping = 0f;
        rb.angularDamping = 0f;
        rb.freezeRotation = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        SphereCollider col = root.AddComponent<SphereCollider>();
        col.radius = 0.3f;

        root.AddComponent<Boid>();

        // Corpo alongado no eixo Z (frente)
        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        Object.DestroyImmediate(body.GetComponent<Collider>());
        body.transform.SetParent(root.transform, false);
        body.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        body.transform.localScale = new Vector3(0.28f, 0.4f, 0.28f);
        ConfigureRenderer(body, layer, bodyMat);

        GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.name = "Head";
        Object.DestroyImmediate(head.GetComponent<Collider>());
        head.transform.SetParent(root.transform, false);
        head.transform.localPosition = new Vector3(0f, 0f, 0.36f);
        head.transform.localScale = Vector3.one * 0.18f;
        ConfigureRenderer(head, layer, headMat);

        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        return saved.GetComponent<Boid>();
    }

    static void ConfigureRenderer(GameObject go, int layer, Material mat)
    {
        go.layer = layer;
        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
    }

    static void MakeObstacle(PrimitiveType type, string name, Vector3 pos, Vector3 scale, Quaternion rot,
                             int layer, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.layer = layer;
        go.isStatic = true;
        go.transform.SetPositionAndRotation(pos, rot);
        go.transform.localScale = scale;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    static Material MakeMaterial(string name, Color color, float smoothness)
    {
        string path = MatDir + "/" + name + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_BaseColor", color);
        mat.color = color;
        mat.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static int EnsureLayer(string name, int preferredIndex)
    {
        int existing = LayerMask.NameToLayer(name);
        if (existing >= 0) return existing;

        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        SerializedProperty layers = tagManager.FindProperty("layers");
        int index = -1;
        if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(preferredIndex).stringValue)) index = preferredIndex;
        else
            for (int i = 8; i < 32; i++)
                if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue)) { index = i; break; }
        if (index < 0) throw new System.Exception("Sem camadas livres para " + name);

        layers.GetArrayElementAtIndex(index).stringValue = name;
        tagManager.ApplyModifiedProperties();
        return index;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }

    static void AddSceneToBuildSettings(string path)
    {
        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        list.RemoveAll(s => s.path == path);
        list.Insert(0, new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = list.ToArray();
    }
}
