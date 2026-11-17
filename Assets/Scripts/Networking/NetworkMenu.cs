using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;

public class NetworkMenu : MonoBehaviour
{
    public static NetworkMenu Instance { get; private set; }

    public static string LocalPlayerName { get; private set; } = "Player";

    public static bool BotMode { get; private set; }

    const string NameKey = "PlayerName";
    const string AddressKey = "JoinAddress";
    const string TokenKey = "SessionToken";
    const int MaxPlayers = 2;

    public ushort port = 7777;
    [Tooltip("Seconds of connection trouble before the Quit button shows up.")]
    public float quitAfter = 10f;
    public float reconnectInterval = 1f;

    [Header("Menu")]
    public GameObject menuPanel;
    public TMP_InputField nameInput;
    public TMP_InputField addressInput;
    public Button hostButton;
    public Button joinButton;
    public TMP_Text messageText;

    [Header("In game")]
    public Button leaveButton;

    [Header("Connection problem overlay")]
    public GameObject connectionPanel;
    public TMP_Text connectionTitle;
    public TMP_Text connectionDetail;
    public Button connectionQuitButton;

    NetworkManager net;
    bool connecting;
    bool joinedMatch;
    bool leaving;

    bool reconnecting;
    double reconnectStarted;
    double nextReconnectAttempt;
    int reconnectAttempts;

    static string token;
    static readonly Dictionary<ulong, string> tokensByClient = new();

    public string HostAddress { get; private set; } = "";

    public static string TokenOf(ulong clientId) => tokensByClient.TryGetValue(clientId, out string t) ? t : null;

    void Awake()
    {
        Instance = this;

        token = PlayerPrefs.GetString(TokenKey, "");
        if (string.IsNullOrEmpty(token))
        {
            token = System.Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(TokenKey, token);
        }
    }

    void Start()
    {
        net = NetworkManager.Singleton;
        net.NetworkConfig.ConnectionApproval = true;
        net.NetworkConfig.TickRate = 60;
        net.ConnectionApprovalCallback = ApproveConnection;
        net.OnClientDisconnectCallback += OnClientDisconnect;
        net.OnClientConnectedCallback += OnClientConnected;

        Transport.HeartbeatTimeoutMS = 500;
        Transport.ConnectTimeoutMS = 1000;
        Transport.MaxConnectAttempts = 5;
        Transport.DisconnectTimeoutMS = 10000;

        nameInput.text = PlayerPrefs.GetString(NameKey, "");
        addressInput.text = PlayerPrefs.GetString(AddressKey, "127.0.0.1");

        hostButton.onClick.AddListener(Host);
        joinButton.onClick.AddListener(Join);
        leaveButton.onClick.AddListener(Leave);
        connectionQuitButton.onClick.AddListener(Leave);

        ShowMenu("");
        HandleCommandLine();
    }

    void HandleCommandLine()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-name" && i + 1 < args.Length) nameInput.text = args[i + 1];
            if (args[i] == "-bot") BotMode = true;
        }

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-join" && i + 1 < args.Length)
            {
                addressInput.text = args[i + 1];
                Join();
                return;
            }
            if (args[i] == "-host")
            {
                Host();
                return;
            }
        }
    }

    void OnDestroy()
    {
        if (net == null) return;
        net.ConnectionApprovalCallback = null;
        net.OnClientDisconnectCallback -= OnClientDisconnect;
        net.OnClientConnectedCallback -= OnClientConnected;
    }

    void Update()
    {
        bool online = net != null && net.IsListening;
        menuPanel.SetActive((!online || connecting) && !reconnecting);
        leaveButton.gameObject.SetActive(online && !connecting && !reconnecting);

        bool canPress = !connecting && !string.IsNullOrWhiteSpace(nameInput.text);
        hostButton.interactable = canPress;
        joinButton.interactable = canPress && !string.IsNullOrWhiteSpace(addressInput.text);

        if (online && !net.IsServer && net.IsConnectedClient && !leaving && !reconnecting &&
            NetClock.SilenceSeconds > Transport.DisconnectTimeoutMS / 1000f)
        {
            StartReconnecting();
            net.Shutdown();
        }

        if (reconnecting) TryReconnect();
        UpdateConnectionOverlay(online);
    }

    void UpdateConnectionOverlay(bool online)
    {
        string title = null, detail = null;
        float seconds = 0f;

        if (reconnecting)
        {
            title = "Connection lost";
            detail = $"Reconnecting to the host... (attempt {reconnectAttempts})";
            seconds = (float)(Time.realtimeSinceStartupAsDouble - reconnectStarted);
        }
        else if (online && !connecting && GameManager.Instance != null)
        {
            GameManager.Instance.GetConnectionProblem(out title, out detail, out seconds);
        }

        connectionPanel.SetActive(title != null);
        if (title == null) return;

        connectionTitle.text = title;
        connectionDetail.text = $"{detail}\n<size=70%>{Mathf.FloorToInt(seconds)}s</size>";
        connectionQuitButton.gameObject.SetActive(seconds >= quitAfter);
    }

    void Host()
    {
        SaveName();
        tokensByClient.Clear();

        Transport.SetConnectionData("127.0.0.1", port, "0.0.0.0");
        HostAddress = $"{GetLocalIPv4()}:{port}";

        if (!net.StartHost())
            ShowMenu($"Couldn't host. Is port {port} already in use?");
    }

    void Join()
    {
        SaveName();

        string address = addressInput.text.Trim();
        ushort joinPort = port;

        int colon = address.LastIndexOf(':');
        if (colon > 0 && ushort.TryParse(address[(colon + 1)..], out ushort parsed))
        {
            joinPort = parsed;
            address = address[..colon];
        }

        if (address == "localhost") address = "127.0.0.1";
        if (!IPAddress.TryParse(address, out _))
        {
            ShowMenu("That isn't a valid IP address.");
            return;
        }

        PlayerPrefs.SetString(AddressKey, addressInput.text.Trim());
        Transport.SetConnectionData(address, joinPort);
        joinedMatch = false;

        if (StartClient())
        {
            connecting = true;
            messageText.text = $"Connecting to {address}:{joinPort}...";
        }
        else
        {
            ShowMenu("Couldn't start the connection.");
        }
    }

    bool StartClient()
    {
        net.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(token);
        return net.StartClient();
    }

    void Leave()
    {
        if (net.IsServer)
        {
            EndSession("");
            return;
        }

        if (reconnecting || !net.IsConnectedClient)
        {
            StopReconnecting();
            joinedMatch = false;
            net.Shutdown();
            ShowMenu("");
            return;
        }

        StartCoroutine(LeaveAsClient());
    }

    IEnumerator LeaveAsClient()
    {
        leaving = true;
        if (GameManager.Instance != null && GameManager.Instance.IsSpawned)
            GameManager.Instance.LeavingRpc();

        yield return new WaitForSecondsRealtime(0.15f);
        net.Shutdown();
        leaving = false;
        ShowMenu("");
    }

    public void EndSession(string message)
    {
        if (net.IsServer)
        {
            foreach (ulong id in net.ConnectedClientsIds)
                if (id != NetworkManager.ServerClientId)
                    net.DisconnectClient(id, "The host ended the match.");
        }

        net.Shutdown();
        ShowMenu(message);
    }

    void StartReconnecting()
    {
        reconnecting = true;
        reconnectStarted = Time.realtimeSinceStartupAsDouble;
        nextReconnectAttempt = reconnectStarted + reconnectInterval;
        reconnectAttempts = 0;
        Debug.Log("[Pong] Connection lost, reconnecting");
    }

    void StopReconnecting()
    {
        reconnecting = false;
    }

    void TryReconnect()
    {
        if (net.IsListening || net.ShutdownInProgress) return;
        if (Time.realtimeSinceStartupAsDouble < nextReconnectAttempt) return;

        reconnectAttempts++;
        nextReconnectAttempt = double.MaxValue;
        if (!StartClient())
            nextReconnectAttempt = Time.realtimeSinceStartupAsDouble + reconnectInterval;
    }

    void ApproveConnection(NetworkManager.ConnectionApprovalRequest request,
                           NetworkManager.ConnectionApprovalResponse response)
    {
        string clientToken = request.Payload != null ? Encoding.UTF8.GetString(request.Payload) : "";
        bool isHostItself = request.ClientNetworkId == NetworkManager.ServerClientId;
        GameManager gm = GameManager.Instance;

        if (isHostItself)
        {
            response.Approved = true;
        }
        else if (gm != null && gm.IsWaitingForReconnect)
        {
            response.Approved = clientToken == gm.AbsentToken;
            response.Reason = "A match is in progress. Waiting for the other player to reconnect.";
        }
        else
        {
            response.Approved = net.ConnectedClientsIds.Count < MaxPlayers;
            response.Reason = "That match is already full.";
        }

        if (response.Approved)
        {
            response.Reason = "";
            tokensByClient[request.ClientNetworkId] = clientToken;
        }
        response.CreatePlayerObject = response.Approved;
    }

    void OnClientConnected(ulong clientId)
    {
        if (clientId != net.LocalClientId) return;

        connecting = false;
        joinedMatch = true;
        if (reconnecting) Debug.Log("[Pong] Reconnected");
        StopReconnecting();
    }

    void OnClientDisconnect(ulong clientId)
    {
        if (net.IsServer) return;
        if (leaving) return;

        string reason = net.DisconnectReason;
        bool connectionBroke = string.IsNullOrEmpty(reason) || reason.StartsWith("[Disconnect Event]");
        net.Shutdown();

        if (connectionBroke && (joinedMatch || reconnecting))
        {
            if (!reconnecting) StartReconnecting();
            nextReconnectAttempt = Time.realtimeSinceStartupAsDouble + reconnectInterval;
            return;
        }

        StopReconnecting();
        if (connectionBroke) reason = "Couldn't connect to the host.";
        ShowMenu(reason);
    }

    UnityTransport Transport => (UnityTransport)net.NetworkConfig.NetworkTransport;

    void ShowMenu(string message)
    {
        connecting = false;
        joinedMatch = false;
        messageText.text = message;
    }

    void SaveName()
    {
        string name = nameInput.text.Trim();
        if (name.Length > 16) name = name[..16];
        LocalPlayerName = name;
        PlayerPrefs.SetString(NameKey, name);
        PlayerPrefs.Save();
    }

    static string GetLocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
        }
        catch
        {
            return "127.0.0.1";
        }
    }
}
