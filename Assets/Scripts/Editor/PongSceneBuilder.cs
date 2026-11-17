using System.IO;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

public static class PongSceneBuilder
{
    const string ArtFolder = "Assets/Art";
    const string PrefabFolder = "Assets/Prefabs";
    const string ScenePath = "Assets/Scenes/Pong.unity";

    static readonly Color FieldColor = new(0.35f, 0.35f, 0.4f);

    static Sprite square, circle;

    [MenuItem("Pong/Rebuild Game Scene")]
    public static void Build()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        Directory.CreateDirectory(ArtFolder);
        Directory.CreateDirectory(PrefabFolder);
        AssetDatabase.DeleteAsset($"{ArtFolder}/Bouncy.physicsMaterial2D");

        square = MakeSprite("Square", 32, false);
        circle = MakeSprite("Circle", 64, true);

        GameObject playerPrefab = MakePlayerPrefab();
        GameObject ballPrefab = MakeBallPrefab("Ball", 0.35f, false);
        GameObject extraBallPrefab = MakeBallPrefab("ExtraBall", 0.25f, true);
        GameObject boxPrefab = MakePowerUpBoxPrefab();

        NetworkPrefabsList prefabList = MakePrefabList(playerPrefab, extraBallPrefab, boxPrefab);

        BuildScene(playerPrefab, ballPrefab, extraBallPrefab, boxPrefab, prefabList);

        Debug.Log("Pong scene built: " + ScenePath);
    }

    static Sprite MakeSprite(string name, int size, bool round)
    {
        string path = $"{ArtFolder}/{name}.png";
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float r = size * 0.5f;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float alpha = 1f;
            if (round)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                alpha = Mathf.Clamp01(r - d);
            }
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
        }

        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);

        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = size;
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.filterMode = FilterMode.Bilinear;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    static Sprite LoadIcon(string name, float size)
    {
        string path = $"{ArtFolder}/PowerUps/{name}.png";
        if (!File.Exists(path)) return null;
        AssetDatabase.ImportAsset(path);
        if (AssetImporter.GetAtPath(path) is not TextureImporter importer) return null;

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = Mathf.Max(tex.width, tex.height) / size;
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.filterMode = FilterMode.Point;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    static GameObject SavePrefab(GameObject go, string name)
    {
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(go, $"{PrefabFolder}/{name}.prefab");
        Object.DestroyImmediate(go);
        return prefab;
    }

    static GameObject MakePlayerPrefab()
    {
        var go = new GameObject("Player");
        go.transform.localScale = new Vector3(PongField.PaddleHalfWidth * 2f, 2f, 1f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = square;

        go.AddComponent<NetworkObject>();

        var paddle = go.AddComponent<PlayerPaddle>();
        paddle.spriteRenderer = sr;
        paddle.baseHeight = 2f;

        return SavePrefab(go, "Player");
    }

    static GameObject MakeBallPrefab(string name, float size, bool isExtra)
    {
        var go = new GameObject(name);
        go.transform.localScale = new Vector3(size, size, 1f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = circle;
        sr.sortingOrder = 2;
        sr.enabled = false;

        go.AddComponent<NetworkObject>();

        var ball = go.AddComponent<Ball>();
        ball.spriteRenderer = sr;
        ball.radius = size * 0.5f;
        ball.isExtraBall = isExtra;

        return SavePrefab(go, name);
    }

    static GameObject MakePowerUpBoxPrefab()
    {
        const float size = 0.9f;
        var go = new GameObject("PowerUpBox");

        var visual = new GameObject("Visual");
        visual.transform.SetParent(go.transform, false);
        visual.transform.localScale = new Vector3(size, size, 1f);
        var sr = visual.AddComponent<SpriteRenderer>();
        sr.sprite = square;
        sr.sortingOrder = 1;

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(go.transform, false);
        var label = labelGo.AddComponent<TextMeshPro>();
        label.text = "x2";
        label.enableAutoSizing = true;
        label.fontSizeMin = 1.5f;
        label.fontSizeMax = 3.5f;
        label.fontStyle = FontStyles.Bold;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.black;
        label.rectTransform.sizeDelta = new Vector2(size - 0.1f, size - 0.1f);
        label.sortingOrder = 3;

        var iconGo = new GameObject("Icon");
        iconGo.transform.SetParent(go.transform, false);
        var icon = iconGo.AddComponent<SpriteRenderer>();
        icon.sortingOrder = 3;
        icon.enabled = false;

        go.AddComponent<NetworkObject>();

        var box = go.AddComponent<PowerUpBox>();
        box.spriteRenderer = sr;
        box.label = label;
        box.halfSize = size * 0.5f;
        box.iconRenderer = icon;
        box.multiBallIcon = LoadIcon("MultiBall", size);
        box.speedUpIcon = LoadIcon("SpeedUp", size);
        box.moveBallIcon = LoadIcon("MoveBall", size);
        box.growIcon = LoadIcon("Grow", size);
        box.catchIcon = LoadIcon("Catch", size);

        return SavePrefab(go, "PowerUpBox");
    }

    static NetworkPrefabsList MakePrefabList(params GameObject[] prefabs)
    {
        string path = $"{PrefabFolder}/PongNetworkPrefabs.asset";
        AssetDatabase.DeleteAsset(path);

        var list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
        foreach (GameObject p in prefabs)
            list.Add(new NetworkPrefab { Prefab = p });

        AssetDatabase.CreateAsset(list, path);
        AssetDatabase.SaveAssets();
        return list;
    }

    static void BuildScene(GameObject playerPrefab, GameObject ballPrefab, GameObject extraBallPrefab,
                           GameObject boxPrefab, NetworkPrefabsList prefabList)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
        camGo.transform.position = new Vector3(0f, 0f, -10f);
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = 5f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.04f, 0.04f, 0.06f);
        camGo.AddComponent<AudioListener>();
        camGo.AddComponent<CameraFit>();

        var netGo = new GameObject("NetworkManager");
        var transport = netGo.AddComponent<UnityTransport>();
        var net = netGo.AddComponent<NetworkManager>();
        net.NetworkConfig = new NetworkConfig
        {
            NetworkTransport = transport,
            PlayerPrefab = playerPrefab,
            ConnectionApproval = true,
            TickRate = 60,
        };
        net.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(prefabList);

        var field = new GameObject("Field");
        float wallCenter = PongField.WallInnerY + PongField.WallThickness * 0.5f;
        MakeBlock("Top Wall", field.transform, new Vector2(0f, wallCenter), new Vector2(40f, PongField.WallThickness), FieldColor);
        MakeBlock("Bottom Wall", field.transform, new Vector2(0f, -wallCenter), new Vector2(40f, PongField.WallThickness), FieldColor);

        var centerLine = new GameObject("Center Line");
        centerLine.transform.SetParent(field.transform, false);
        for (float y = -4.25f; y <= 4.3f; y += 0.75f)
            MakeBlock("Dash", centerLine.transform, new Vector2(0f, y), new Vector2(0.08f, 0.4f),
                      new Color(1f, 1f, 1f, 0.15f));

        var ballGo = (GameObject)PrefabUtility.InstantiatePrefab(ballPrefab);
        ballGo.transform.position = Vector3.zero;

        var gmGo = new GameObject("GameManager");
        gmGo.AddComponent<NetworkObject>();
        gmGo.AddComponent<NetClock>();
        var gm = gmGo.AddComponent<GameManager>();
        gm.ball = ballGo.GetComponent<Ball>();

        var pumGo = new GameObject("PowerUpManager");
        pumGo.AddComponent<NetworkObject>();
        var pum = pumGo.AddComponent<PowerUpManager>();
        pum.boxPrefab = boxPrefab.GetComponent<PowerUpBox>();
        pum.extraBallPrefab = extraBallPrefab.GetComponent<Ball>();

        BuildUI(gm, netGo);

        EditorSceneManager.SaveScene(scene, ScenePath);

        var validate = typeof(NetworkObject).GetMethod("OnValidate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        foreach (NetworkObject no in Object.FindObjectsByType<NetworkObject>(FindObjectsInactive.Include))
        {
            validate?.Invoke(no, null);
            EditorUtility.SetDirty(no);
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
    }

    static GameObject MakeBlock(string name, Transform parent, Vector2 pos, Vector2 size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = new Vector3(size.x, size.y, 1f);

        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = square;
        sr.color = color;
        return go;
    }

    static void BuildUI(GameManager gm, GameObject netGo)
    {
        var es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
        es.AddComponent<InputSystemUIInputModule>();

        var canvasGo = new GameObject("Canvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();
        Transform root = canvasGo.transform;

        RectTransform hud = Stretch(new GameObject("HUD", typeof(RectTransform)), root);

        gm.hud = hud.gameObject;
        gm.redScoreText = Text("Red Score", hud, "Red\n0", 64, Color.red, TextAlignmentOptions.TopLeft,
                               new Vector2(0f, 1f), new Vector2(40f, -30f), new Vector2(700f, 300f));
        gm.blueScoreText = Text("Blue Score", hud, "Blue\n0", 64, new Color(0.3f, 0.5f, 1f), TextAlignmentOptions.TopRight,
                                new Vector2(1f, 1f), new Vector2(-40f, -30f), new Vector2(700f, 300f));
        gm.statusText = Text("Status", hud, "Waiting for opponent...", 56, Color.white, TextAlignmentOptions.Center,
                             new Vector2(0.5f, 0.5f), new Vector2(0f, 200f), new Vector2(1400f, 250f));
        gm.pingText = Text("Ping", hud, "", 28, new Color(1f, 1f, 1f, 0.45f), TextAlignmentOptions.BottomLeft,
                           Vector2.zero, new Vector2(30f, 20f), new Vector2(400f, 50f));

        RectTransform over = Stretch(new GameObject("Game Over Panel", typeof(RectTransform)), root);
        over.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.75f);
        gm.gameOverPanel = over.gameObject;
        gm.winnerText = Text("Winner", over, "Red wins!", 110, Color.red, TextAlignmentOptions.Center,
                             Center, new Vector2(0f, 180f), new Vector2(1600f, 200f));

        var hostButtons = new GameObject("Host Rematch", typeof(RectTransform));
        RectTransform hb = Stretch(hostButtons, over);
        Text("Rematch Label", hb, "Rematch?", 60, Color.white, TextAlignmentOptions.Center,
             Center, new Vector2(0f, 20f), new Vector2(800f, 100f));
        gm.rematchYesButton = MakeButton("Yes", hb, "Yes", new Vector2(-170f, -120f));
        gm.rematchNoButton = MakeButton("No", hb, "No", new Vector2(170f, -120f));
        gm.hostRematchButtons = hostButtons;

        gm.waitingForHostText = Text("Waiting For Host", over, "Waiting for the host to decide on a rematch...", 48,
                                     Color.white, TextAlignmentOptions.Center, Center, new Vector2(0f, -40f),
                                     new Vector2(1400f, 100f)).gameObject;
        over.gameObject.SetActive(false);

        var menu = netGo.AddComponent<NetworkMenu>();

        menu.leaveButton = MakeButton("Leave", root, "Leave", Vector2.zero, new Vector2(200f, 70f), 36);
        var leaveRt = (RectTransform)menu.leaveButton.transform;
        leaveRt.anchorMin = leaveRt.anchorMax = leaveRt.pivot = new Vector2(0.5f, 1f);
        leaveRt.anchoredPosition = new Vector2(0f, -30f);

        RectTransform conn = Stretch(new GameObject("Connection Panel", typeof(RectTransform)), root);
        conn.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.8f);
        menu.connectionPanel = conn.gameObject;
        menu.connectionTitle = Text("Title", conn, "Connection problem", 72, new Color(1f, 0.8f, 0.3f),
                                    TextAlignmentOptions.Center, Center, new Vector2(0f, 120f), new Vector2(1600f, 120f));
        menu.connectionTitle.fontStyle = FontStyles.Bold;
        menu.connectionDetail = Text("Detail", conn, "Waiting...", 44, Color.white, TextAlignmentOptions.Center,
                                     Center, new Vector2(0f, -10f), new Vector2(1600f, 160f));
        menu.connectionQuitButton = MakeButton("Quit", conn, "Quit", new Vector2(0f, -170f));
        conn.gameObject.SetActive(false);

        RectTransform panel = Stretch(new GameObject("Menu Panel", typeof(RectTransform)), root);
        panel.gameObject.AddComponent<Image>().color = new Color(0.04f, 0.04f, 0.06f, 0.97f);
        menu.menuPanel = panel.gameObject;

        Text("Title", panel, "PONG MASTERS <color=#ff4040>ON</color><color=#4d80ff>LINE</color>", 110, Color.white,
             TextAlignmentOptions.Center, Center, new Vector2(0f, 340f), new Vector2(1800f, 160f)).fontStyle = FontStyles.Bold;

        Text("Name Label", panel, "Your name", 40, new Color(1f, 1f, 1f, 0.7f), TextAlignmentOptions.Left,
             Center, new Vector2(0f, 175f), new Vector2(640f, 60f));
        menu.nameInput = MakeInput("Name Input", panel, "Enter your name...", new Vector2(0f, 115f));
        menu.nameInput.characterLimit = 16;

        Text("Address Label", panel, "Join code or host IP", 40, new Color(1f, 1f, 1f, 0.7f), TextAlignmentOptions.Left,
             Center, new Vector2(0f, 20f), new Vector2(640f, 60f));
        menu.addressInput = MakeInput("Address Input", panel, "127.0.0.1", new Vector2(0f, -40f));

        menu.hostButton = MakeButton("Host Button", panel, "Host", new Vector2(-165f, -170f));
        menu.joinButton = MakeButton("Join Button", panel, "Join", new Vector2(165f, -170f));

        menu.messageText = Text("Message", panel, "", 40, new Color(1f, 0.8f, 0.3f), TextAlignmentOptions.Center,
                                Center, new Vector2(0f, -280f), new Vector2(1400f, 80f));
    }

    static readonly Vector2 Center = new(0.5f, 0.5f);

    static RectTransform Stretch(GameObject go, Transform parent)
    {
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return rt;
    }

    static TextMeshProUGUI Text(string name, Transform parent, string text, float size, Color color,
                                TextAlignmentOptions align, Vector2 anchor, Vector2 pos, Vector2 boxSize)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.raycastTarget = false;

        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos;
        rt.sizeDelta = boxSize;
        return t;
    }

    static Button MakeButton(string name, Transform parent, string label, Vector2 pos)
        => MakeButton(name, parent, label, pos, new Vector2(300f, 90f), 44);

    static Button MakeButton(string name, Transform parent, string label, Vector2 pos, Vector2 size, float fontSize)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        var img = go.AddComponent<Image>();
        img.color = Color.white;
        var button = go.AddComponent<Button>();
        var colors = button.colors;
        colors.normalColor = new Color(0.2f, 0.2f, 0.26f);
        colors.highlightedColor = new Color(0.3f, 0.3f, 0.4f);
        colors.pressedColor = new Color(0.15f, 0.15f, 0.2f);
        colors.selectedColor = colors.normalColor;
        colors.disabledColor = new Color(0.12f, 0.12f, 0.14f);
        button.colors = colors;

        TextMeshProUGUI text = Text("Label", go.transform, label, fontSize, Color.white, TextAlignmentOptions.Center,
                                    Center, Vector2.zero, size);
        text.fontStyle = FontStyles.Bold;
        return button;
    }

    static TMP_InputField MakeInput(string name, Transform parent, string placeholder, Vector2 pos)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(640f, 80f);
        go.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.2f);

        var area = new GameObject("Text Area", typeof(RectTransform));
        RectTransform areaRt = Stretch(area, go.transform);
        areaRt.offsetMin = new Vector2(20f, 8f);
        areaRt.offsetMax = new Vector2(-20f, -8f);
        area.AddComponent<RectMask2D>();

        TextMeshProUGUI ph = Text("Placeholder", areaRt, placeholder, 38, new Color(1f, 1f, 1f, 0.3f),
                                  TextAlignmentOptions.Left, Center, Vector2.zero, Vector2.zero);
        Stretch(ph.gameObject, areaRt);
        ph.fontStyle = FontStyles.Italic;

        TextMeshProUGUI txt = Text("Text", areaRt, "", 38, Color.white, TextAlignmentOptions.Left,
                                   Center, Vector2.zero, Vector2.zero);
        Stretch(txt.gameObject, areaRt);

        var input = go.AddComponent<TMP_InputField>();
        input.textViewport = areaRt;
        input.textComponent = txt;
        input.placeholder = ph;
        input.pointSize = 38;
        input.caretWidth = 3;
        input.customCaretColor = true;
        input.caretColor = Color.white;
        return input;
    }
}
