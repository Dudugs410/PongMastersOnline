using System.Collections.Generic;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

public enum PlayerSide { Red = 1, Blue = 2 }

public enum GamePhase : byte
{
    WaitingForOpponent,
    Playing,
    Frozen,
    OpponentLeft,
    Resuming,
    MatchOver
}

public class GameManager : NetworkBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Rules")]
    public int winningScore = 10;
    public float resumeCountdown = 3f;

    [Header("References")]
    public Ball ball;

    [Header("HUD")]
    public GameObject hud;
    public TMP_Text redScoreText;
    public TMP_Text blueScoreText;
    public TMP_Text statusText;
    public TMP_Text pingText;

    [Header("Game Over")]
    public GameObject gameOverPanel;
    public TMP_Text winnerText;
    public GameObject hostRematchButtons;
    public GameObject waitingForHostText;
    public Button rematchYesButton;
    public Button rematchNoButton;

    readonly NetworkVariable<int> redScore = new(0);
    readonly NetworkVariable<int> blueScore = new(0);
    readonly NetworkVariable<GamePhase> phase = new(GamePhase.WaitingForOpponent);
    readonly NetworkVariable<PlayerSide> winner = new(PlayerSide.Red);
    readonly NetworkVariable<double> phaseStartedAt = new(0);
    readonly NetworkVariable<double> resumeAt = new(0);
    readonly NetworkVariable<FixedString64Bytes> opponentName = new(default);

    readonly HashSet<ulong> leftOnPurpose = new();
    double frozenSince;
    string absentToken;

    public GamePhase Phase => phase.Value;
    public bool IsPlaying => IsSpawned && phase.Value == GamePhase.Playing;

    public bool IsWaitingForReconnect => IsServer && IsSpawned && phase.Value == GamePhase.OpponentLeft;
    public string AbsentToken => absentToken;

    bool OpponentConnected => NetworkManager.ConnectedClientsIds.Count >= 2;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        rematchYesButton.onClick.AddListener(OnRematchYes);
        rematchNoButton.onClick.AddListener(OnRematchNo);
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        NetworkManager.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        leftOnPurpose.Clear();
        absentToken = null;
        redScore.Value = 0;
        blueScore.Value = 0;
        SetPhase(GamePhase.WaitingForOpponent);
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager != null)
        {
            NetworkManager.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }

    void OnClientConnected(ulong clientId)
    {
        if (clientId == NetworkManager.ServerClientId) return;

        if (phase.Value == GamePhase.OpponentLeft)
        {
            absentToken = null;
            Log("Opponent reconnected");
            BeginResume();
        }
        else if (OpponentConnected)
        {
            StartMatch();
        }
    }

    void OnClientDisconnected(ulong clientId)
    {
        if (clientId == NetworkManager.ServerClientId) return;
        NetClock.Instance.ForgetPeer();

        bool midMatch = phase.Value is GamePhase.Playing or GamePhase.Frozen or GamePhase.Resuming;
        bool onPurpose = leftOnPurpose.Remove(clientId);

        if (midMatch && !onPurpose)
        {
            absentToken = NetworkMenu.TokenOf(clientId);
            if (phase.Value != GamePhase.Frozen) FreezeAll();
            SetPhase(GamePhase.OpponentLeft);
            Log("Opponent lost, waiting for reconnection");
        }
        else
        {
            ResetToWaiting();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void LeavingRpc(RpcParams rpcParams = default)
    {
        leftOnPurpose.Add(rpcParams.Receive.SenderClientId);
    }

    void SetPhase(GamePhase p)
    {
        phase.Value = p;
        phaseStartedAt.Value = NetClock.Now;
    }

    void StartMatch()
    {
        redScore.Value = 0;
        blueScore.Value = 0;
        PowerUpManager.Instance.ClearAll();
        ball.Serve();
        SetPhase(GamePhase.Playing);
    }

    void ResetToWaiting()
    {
        redScore.Value = 0;
        blueScore.Value = 0;
        absentToken = null;
        ball.StopBall();
        PowerUpManager.Instance.ClearAll();
        SetPhase(GamePhase.WaitingForOpponent);
    }

    void FreezeAll()
    {
        frozenSince = NetClock.Now - Mathf.Min(NetClock.SilenceSeconds, 1f);
        foreach (Ball b in Ball.All.ToArray())
            b.Freeze(frozenSince);
    }

    void BeginResume()
    {
        double at = NetClock.Now + resumeCountdown;
        double frozenFor = at - frozenSince;

        foreach (Ball b in Ball.All.ToArray())
            b.Unfreeze(at, frozenFor);

        if (PlayerPaddle.Red != null) PlayerPaddle.Red.ExtendEffects(frozenFor);
        if (PlayerPaddle.Blue != null) PlayerPaddle.Blue.ExtendEffects(frozenFor);

        resumeAt.Value = at;
        SetPhase(GamePhase.Resuming);
    }

    void Update()
    {
        if (IsServer && IsSpawned) ServerUpdate();
        UpdateUI();
    }

    void ServerUpdate()
    {
        if (PlayerPaddle.Blue != null)
            opponentName.Value = new FixedString64Bytes(PlayerPaddle.Blue.DisplayName);

        switch (phase.Value)
        {
            case GamePhase.Playing when NetClock.PeerIsSilent:
                FreezeAll();
                SetPhase(GamePhase.Frozen);
                Log("Connection problem, game frozen");
                break;

            case GamePhase.Resuming when NetClock.PeerIsSilent:
                FreezeAll();
                SetPhase(GamePhase.Frozen);
                break;

            case GamePhase.Frozen when !NetClock.PeerIsSilent:
                Log("Connection recovered, resuming");
                BeginResume();
                break;

            case GamePhase.Resuming when NetClock.Now >= resumeAt.Value:
                SetPhase(GamePhase.Playing);
                break;
        }
    }

    void LateUpdate()
    {
        if (!IsServer || !IsPlaying) return;

        double now = NetClock.Now;
        foreach (Ball b in Ball.All.ToArray())
        {
            BallState s = b.State;
            if (s.mode != BallMode.Moving) continue;

            Vector2 p = b.PositionAt(now);
            PlayerSide defender;
            if (p.x < -PongField.GoalLineX) defender = PlayerSide.Red;
            else if (p.x > PongField.GoalLineX) defender = PlayerSide.Blue;
            else continue;

            PlayerPaddle paddle = PlayerPaddle.Get(defender);
            if (paddle != null && !paddle.IsOwnedByServer)
            {
                if (NetClock.SilenceSeconds > 0.25f) continue;

                double contact = s.TimeAtX(PongField.ContactX(defender, b.radius));
                double grace = Mathf.Clamp(NetClock.Rtt + 0.1f, 0.1f, 0.6f);
                if (now < contact + grace) continue;
            }

            AddPoint(defender == PlayerSide.Red ? PlayerSide.Blue : PlayerSide.Red, b);
            if (!IsPlaying) return;
        }
    }

    void AddPoint(PlayerSide side, Ball scoringBall)
    {
        Log($"Point to {side} ({(scoringBall.isExtraBall ? "extra" : "main")} ball, {(scoringBall.State.fastFrom != 0 ? "fast shot" : "normal")})");

        int newScore;
        if (side == PlayerSide.Red) newScore = ++redScore.Value;
        else newScore = ++blueScore.Value;

        if (newScore >= winningScore)
        {
            winner.Value = side;
            ball.StopBall();
            PowerUpManager.Instance.ClearAll();
            SetPhase(GamePhase.MatchOver);
            return;
        }

        if (scoringBall.isExtraBall)
        {
            PowerUpManager.Instance.RemoveExtraBall(scoringBall);
        }
        else
        {
            PowerUpManager.Instance.ClearExtraBalls();
            ball.Serve();
        }
    }

    void OnRematchYes()
    {
        if (!IsServer || !OpponentConnected) return;
        StartMatch();
    }

    void OnRematchNo()
    {
        if (!IsServer) return;
        NetworkMenu.Instance.EndSession("Match ended.");
    }

    static void Log(string message) => Debug.Log("[Pong] " + message);

    public bool GetConnectionProblem(out string title, out string detail, out float seconds)
    {
        title = detail = null;
        seconds = 0f;
        if (!IsSpawned) return false;

        string opponent = opponentName.Value.ToString();
        if (string.IsNullOrEmpty(opponent)) opponent = "your opponent";

        if (!IsServer && NetClock.PeerIsSilent)
        {
            title = "Connection problem";
            detail = "Waiting for the connection to the host to recover...";
            seconds = NetClock.SilenceSeconds;
            return true;
        }

        float inPhase = (float)(NetClock.Now - phaseStartedAt.Value);
        switch (phase.Value)
        {
            case GamePhase.Frozen:
                title = "Connection problem";
                detail = IsServer
                    ? $"Waiting for {opponent}'s connection to recover..."
                    : "The host can't hear you. Waiting for the connection to recover...";
                seconds = inPhase;
                return true;

            case GamePhase.OpponentLeft:
                title = $"{opponent} disconnected";
                detail = "Waiting for them to reconnect...";
                seconds = inPhase;
                return true;
        }
        return false;
    }

    void UpdateUI()
    {
        hud.SetActive(IsSpawned);

        if (!IsSpawned)
        {
            gameOverPanel.SetActive(false);
            return;
        }

        redScoreText.text = ScoreLine(PlayerSide.Red, redScore.Value);
        blueScoreText.text = ScoreLine(PlayerSide.Blue, blueScore.Value);

        statusText.text = phase.Value switch
        {
            GamePhase.WaitingForOpponent when IsServer =>
                $"Waiting for opponent...\n<size=60%>Your address: {NetworkMenu.Instance.HostAddress}</size>",
            GamePhase.WaitingForOpponent => "Waiting for opponent...",
            GamePhase.Resuming => $"Resuming in {Mathf.CeilToInt((float)(resumeAt.Value - NetClock.Now))}",
            _ => ""
        };

        bool online = NetworkManager.ConnectedClientsIds.Count >= 2 || !IsServer;
        pingText.text = online && phase.Value != GamePhase.WaitingForOpponent
            ? $"Ping {Mathf.RoundToInt(NetClock.Rtt * 1000f)} ms"
            : "";

        bool over = phase.Value == GamePhase.MatchOver;
        gameOverPanel.SetActive(over);
        if (over)
        {
            winnerText.text = $"{NameOf(winner.Value)} wins!";
            winnerText.color = winner.Value == PlayerSide.Red ? Color.red : new Color(0.3f, 0.5f, 1f);
            hostRematchButtons.SetActive(IsServer);
            waitingForHostText.SetActive(!IsServer);
        }
    }

    static string ScoreLine(PlayerSide side, int score)
    {
        PlayerPaddle p = PlayerPaddle.Get(side);
        string effects = "";

        if (p != null)
        {
            if (p.HasSpeedBoost) effects += $"SPEED {Mathf.CeilToInt(p.SpeedBoostTimeLeft)}s  ";
            if (p.HasMoveBall) effects += $"AIM {Mathf.CeilToInt(p.MoveBallTimeLeft)}s  ";
            if (p.HasGrow) effects += $"BIG {Mathf.CeilToInt(p.GrowTimeLeft)}s  ";
            if (p.HasCatch) effects += "CATCH";
        }

        return $"{NameOf(side)}\n{score}\n<size=60%>{effects}</size>";
    }

    static string NameOf(PlayerSide side)
    {
        PlayerPaddle p = PlayerPaddle.Get(side);
        return p != null ? p.DisplayName : side.ToString();
    }
}
