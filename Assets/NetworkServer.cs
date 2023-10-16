using UnityEngine;
using UnityEngine.Assertions;
using Unity.Collections;
using Unity.Networking.Transport;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEditor;

//! NETWORK SERVER

public class NetworkServer : MonoBehaviour
{
    public NetworkDriver networkDriver;
    private NativeList<NetworkConnection> networkConnections;

    NetworkPipeline reliableAndInOrderPipeline;
    NetworkPipeline nonReliableNotInOrderedPipeline;

    const ushort NetworkPort = 9001;

    const int MaxNumberOfClientConnections = 1000;

    LinkedList<PlayerAccount> playerAccounts;
    LinkedList<GameRoom> gameRooms;

    //int OnQueuePlayerIndex = -1;

    void Start()
    {
        networkDriver = NetworkDriver.Create();
        reliableAndInOrderPipeline = networkDriver.CreatePipeline(typeof(FragmentationPipelineStage), typeof(ReliableSequencedPipelineStage));
        nonReliableNotInOrderedPipeline = networkDriver.CreatePipeline(typeof(FragmentationPipelineStage));
        NetworkEndPoint endpoint = NetworkEndPoint.AnyIpv4;
        endpoint.Port = NetworkPort;

        int error = networkDriver.Bind(endpoint);
        if (error != 0)
            Debug.Log("Failed to bind to port " + NetworkPort);
        else
            networkDriver.Listen();

        networkConnections = new NativeList<NetworkConnection>(MaxNumberOfClientConnections, Allocator.Persistent);

        playerAccounts = new LinkedList<PlayerAccount>();
        LoadPlayersAccounts();
        gameRooms = new LinkedList<GameRoom>();
    }

    void OnDestroy()
    {
        networkDriver.Dispose();
        networkConnections.Dispose();
    }

    void Update()
    {
        #region Check Input and Send Msg

        // if (Input.GetKeyDown(KeyCode.A))
        // {
        //     for (int i = 0; i < networkConnections.Length; i++)
        //     {
        //         SendMessageToClient("Hello client's world, sincerely your network server", networkConnections[i]);
        //     }
        // }

        #endregion

        networkDriver.ScheduleUpdate().Complete();

        #region Remove Unused Connections

        for (int i = 0; i < networkConnections.Length; i++)
        {
            if (!networkConnections[i].IsCreated)
            {
                networkConnections.RemoveAtSwapBack(i);
                i--;
            }
        }

        #endregion

        #region Accept New Connections

        while (AcceptIncomingConnection())
        {
            Debug.Log("Accepted a client connection");
        }

        #endregion

        #region Manage Network Events

        DataStreamReader streamReader;
        NetworkPipeline pipelineUsedToSendEvent;
        NetworkEvent.Type networkEventType;

        for (int i = 0; i < networkConnections.Length; i++)
        {
            if (!networkConnections[i].IsCreated)
                continue;

            while (PopNetworkEventAndCheckForData(networkConnections[i], out networkEventType, out streamReader, out pipelineUsedToSendEvent))
            {
                if (pipelineUsedToSendEvent == reliableAndInOrderPipeline)
                    Debug.Log("Network event from: reliableAndInOrderPipeline");
                else if (pipelineUsedToSendEvent == nonReliableNotInOrderedPipeline)
                    Debug.Log("Network event from: nonReliableNotInOrderedPipeline");

                switch (networkEventType)
                {
                    case NetworkEvent.Type.Data:
                        int sizeOfDataBuffer = streamReader.ReadInt();
                        NativeArray<byte> buffer = new NativeArray<byte>(sizeOfDataBuffer, Allocator.Persistent);
                        streamReader.ReadBytes(buffer);
                        byte[] byteBuffer = buffer.ToArray();
                        string msg = Encoding.Unicode.GetString(byteBuffer);
                        ProcessReceivedMsg(msg, i);
                        buffer.Dispose();
                        break;
                    case NetworkEvent.Type.Disconnect:
                        Debug.Log("Client has disconnected from server");
                        networkConnections[i] = default(NetworkConnection);
                        break;
                }
            }
        }

        #endregion
    }

    private bool AcceptIncomingConnection()
    {
        NetworkConnection connection = networkDriver.Accept();
        if (connection == default(NetworkConnection))
            return false;

        networkConnections.Add(connection);
        return true;
    }

    private bool PopNetworkEventAndCheckForData(NetworkConnection networkConnection, out NetworkEvent.Type networkEventType, out DataStreamReader streamReader, out NetworkPipeline pipelineUsedToSendEvent)
    {
        networkEventType = networkConnection.PopEvent(networkDriver, out streamReader, out pipelineUsedToSendEvent);

        if (networkEventType == NetworkEvent.Type.Empty)
            return false;
        return true;
    }

    private void ProcessReceivedMsg(string msg, int connectionIndex)
    {
        Debug.Log("Msg received = " + msg);

        string[] csv = msg.Split(',');
        int signifier = int.Parse(csv[0]);

        if (signifier == ClientToServerSignifiers.CreateAccount)
        {
            Debug.Log("Creating account...");
            string u = csv[1];
            string p = csv[2];
            var filteredAccounts = playerAccounts.Where(account => account.Username == u);
            bool accountExists = filteredAccounts.Any();
            if (accountExists)
            {
                Debug.Log("Account already exists...");
                SendMessageToClient($"{ServerToClientSignifiers.AccountCreationFailed}", networkConnections[connectionIndex]);
            }
            else
            {
                Debug.Log("Account successfully created...");
                PlayerAccount newPlayerAccount = new PlayerAccount(u, p);
                playerAccounts.AddLast(newPlayerAccount);
                SendMessageToClient($"{ServerToClientSignifiers.AccountCreationComplete}", networkConnections[connectionIndex]);
                SavePlayersAccounts();
            }
        }
        else if (signifier == ClientToServerSignifiers.Login)
        {
            Debug.Log("Login account...");
            string u = csv[1];
            string p = csv[2];
            var filteredAccounts = playerAccounts.Where(account => account.Username == u);
            bool accountExists = filteredAccounts.Any();
            if (accountExists)
            {
                PlayerAccount matchedAccount = filteredAccounts.FirstOrDefault();
                if (matchedAccount.Password == p)
                {
                    SendMessageToClient($"{ServerToClientSignifiers.LoginComplete}", networkConnections[connectionIndex]);
                    Debug.Log("Login Complete!");
                }
                else
                {
                    Debug.Log("Incorrect password...");
                    SendMessageToClient($"{ServerToClientSignifiers.LoginFailed}", networkConnections[connectionIndex]);
                }
            }
            else
            {
                Debug.Log("Username does not exist...");
                SendMessageToClient($"{ServerToClientSignifiers.LoginFailed}", networkConnections[connectionIndex]);
            }
        }
        else if (signifier == ClientToServerSignifiers.OnQueue)
        {
            string grName = csv[1];
            var filteredGameRoomsNames = gameRooms.Where(gr => gr.Name == grName);
            bool gameRoomNameExists = filteredGameRoomsNames.Any();

            if (gameRoomNameExists)
            {
                Debug.Log("Player join gameRoom...");

                GameRoom foundRoom = null;
                foreach (var gr in gameRooms)
                {
                    if (gr.Name == grName)
                    {
                        foundRoom = gr;
                        break;
                    }
                }
                foundRoom.ncP2 = networkConnections[connectionIndex];
                foundRoom.hasStarted = true;
                SendMessageToClient($"{ServerToClientSignifiers.GameRoomCreated}" + ",X," + $"{foundRoom.ncViewers.Count()}", foundRoom.ncP1);
                SendMessageToClient($"{ServerToClientSignifiers.GameRoomCreated}" + ",O," + $"{foundRoom.ncViewers.Count()}", foundRoom.ncP2);
                GameplaySetUp(foundRoom);
                foreach (var ncv in foundRoom.ncViewers)
                {
                    SendMessageToClient($"{ServerToClientSignifiers.GameRoomCreated}" + ",V," + $"{foundRoom.ncViewers.Count()}", ncv);
                }
            }

            else
            {
                Debug.Log("gr created...");
                GameRoom gr = new GameRoom(grName);
                gr.ncP1 = networkConnections[connectionIndex];
                gameRooms.AddLast(gr);
                SendMessageToClient($"{ServerToClientSignifiers.WaitForOpponent}", gr.ncP1);
            }
        }
        else if (signifier == ClientToServerSignifiers.OnQueueAsViewer)
        {
            string grName = csv[1];
            var filteredGameRoomsNames = gameRooms.Where(gr => gr.Name == grName);
            foreach (var gr in gameRooms)
            {
                if (gr.Name == grName)
                {
                    if (gr.hasStarted)
                        return;
                    gr.ncViewers.AddLast(networkConnections[connectionIndex]);
                    break;

                }
            }
        }
        else if (signifier == ClientToServerSignifiers.LeaveQueue)
        {
            GameRoom foundRoom = null;
            GameRoom viewerInRoom = null;
            foreach (var gr in gameRooms)
            {
                if (gr.ncP1 == networkConnections[connectionIndex])
                {
                    foundRoom = gr;
                    Debug.Log("Game room found for deleted...");
                    break;
                }
                foreach (var ncv in gr.ncViewers)
                {
                    if (ncv == networkConnections[connectionIndex])
                    {
                        viewerInRoom = gr;
                        break;
                    }
                }
            }
            if (foundRoom != null)
            {
                gameRooms.Remove(foundRoom);
                Debug.Log("Game room removed..");
            }
            if (viewerInRoom != null)
            {
                viewerInRoom.ncViewers.Remove(networkConnections[connectionIndex]);
                Debug.Log("Deleted viewer");
            }
        }
        else if (signifier == ClientToServerSignifiers.Surrender)
        {
            GameRoom completedRoom = null;
            foreach (var gr in gameRooms)
            {
                if (gr.IsPlayingOnGameRoom(networkConnections[connectionIndex]))
                {
                    SendMessageToClient($"{ServerToClientSignifiers.YouWin}", gr.GetOpponenetNetworkConnection(networkConnections[connectionIndex]));
                    //SendMessageToClient($"{ServerToClientSignifiers.YouLose},E", networkConnections[connectionIndex]);
                    completedRoom = gr;
                    Debug.Log("Game room removed..");
                    break;
                }
            }
            gameRooms.Remove(completedRoom);
        }
        else if (signifier == ClientToServerSignifiers.MyMove)
        {
            foreach (var gr in gameRooms)
            {
                if (gr.IsPlayingOnGameRoom(networkConnections[connectionIndex]))
                {
                    SendMessageToClient($"{ServerToClientSignifiers.YourTurn}" + "," + csv[1],
                    gr.GetOpponenetNetworkConnection(networkConnections[connectionIndex]));
                    Debug.Log("Room Found in ClientToServerSignifiers.MyMove");
                    foreach (var ncv in gr.ncViewers)
                    {
                        if (gr.ncP1 == networkConnections[connectionIndex])
                            SendMessageToClient($"{ServerToClientSignifiers.UpdateForViewers}" + "," + csv[1] + ",p1", ncv);
                        else
                            SendMessageToClient($"{ServerToClientSignifiers.UpdateForViewers}" + "," + csv[1] + ",p2", ncv);

                    }
                    break;
                }
                Debug.Log("Room NOT found from ClientToServerSignifiers.MyMove");
            }
        }
        else if (signifier == ClientToServerSignifiers.PlayerWin)
        {
            GameRoom completedRoom = null;
            foreach (var gr in gameRooms)
            {
                if (gr.IsPlayingOnGameRoom(networkConnections[connectionIndex]))
                {
                    SendMessageToClient($"{ServerToClientSignifiers.YouWin}", networkConnections[connectionIndex]);
                    SendMessageToClient($"{ServerToClientSignifiers.YouLose}" + "," + csv[1], gr.GetOpponenetNetworkConnection(networkConnections[connectionIndex]));
                    completedRoom = gr;
                    break;
                }
            }
            gameRooms.Remove(completedRoom);
        }
        else if (signifier == ClientToServerSignifiers.UseChatWheel)
        {
            foreach (var gr in gameRooms)
            {
                if (gr.IsPlayingOnGameRoom(networkConnections[connectionIndex]))
                {
                    SendMessageToClient($"{ServerToClientSignifiers.OpponentChatWheel}" + "," + csv[1],
                    gr.GetOpponenetNetworkConnection(networkConnections[connectionIndex]));
                    Debug.Log("SendMessage to opponent" + csv[1]);
                    break;
                }
            }
        }
    }

    public void SendMessageToClient(string msg, NetworkConnection networkConnection)
    {
        byte[] msgAsByteArray = Encoding.Unicode.GetBytes(msg);
        NativeArray<byte> buffer = new NativeArray<byte>(msgAsByteArray, Allocator.Persistent);


        //Driver.BeginSend(m_Connection, out var writer);
        DataStreamWriter streamWriter;
        //networkConnection.
        networkDriver.BeginSend(reliableAndInOrderPipeline, networkConnection, out streamWriter);
        streamWriter.WriteInt(buffer.Length);
        streamWriter.WriteBytes(buffer);
        networkDriver.EndSend(streamWriter);

        buffer.Dispose();
    }

    public void SavePlayersAccounts()
    {
        using (StreamWriter sw = new StreamWriter(Application.dataPath + Path.DirectorySeparatorChar + "PlayerAccounts.txt"))
        {
            foreach (PlayerAccount pa in playerAccounts)
            {
                sw.WriteLine(pa.Username + "," + pa.Password);
            }
        }
    }

    public void LoadPlayersAccounts()
    {
        if (!File.Exists(Application.dataPath + Path.DirectorySeparatorChar + "PlayerAccounts.txt"))
            return;

        using (StreamReader sr = new StreamReader(Application.dataPath + Path.DirectorySeparatorChar + "PlayerAccounts.txt"))
        {
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                string[] csv = line.Split(',');
                PlayerAccount newPlayerAccount = new PlayerAccount(csv[0], csv[1]);
                playerAccounts.AddLast(newPlayerAccount);
            }
        }
    }

    public void GameplaySetUp(GameRoom gr)
    {
        //SendMessageToClient($"{ServerToClientSignifiers.YourTurn}" + ",E", gr.ncP1);
        int randomPlayer = Random.Range(0, 2);
        if (randomPlayer == 0)
        {
            Debug.Log("Player 1 starts...");
            SendMessageToClient($"{ServerToClientSignifiers.YourTurn}" + ",E", gr.ncP1);
        }
        else
        {
            Debug.Log("Player 2 starts...");
            SendMessageToClient($"{ServerToClientSignifiers.YourTurn}" + ",E", gr.ncP2);
        }
    }

}

public class PlayerAccount
{
    public string Username { get; set; }
    public string Password { get; set; }
    public PlayerAccount(string username, string password)
    {
        Username = username;
        Password = password;
    }
}

public class GameRoom
{
    public string Name { get; set; }
    public NetworkConnection ncP1 { get; set; }
    public NetworkConnection ncP2 { get; set; }
    public bool hasStarted = false;
    public LinkedList<NetworkConnection> ncViewers;

    public GameRoom(string name)
    {
        Name = name;
        ncViewers = new LinkedList<NetworkConnection>();
    }

    public NetworkConnection GetOpponenetNetworkConnection(NetworkConnection pc)
    {
        if (ncP1 == pc)
            return ncP2;
        return ncP1;
    }
    public bool IsPlayingOnGameRoom(NetworkConnection pc)
    {
        if (ncP1 == pc || ncP2 == pc)
            return true;
        return false;
    }

}

public static class ClientToServerSignifiers
{
    public const int CreateAccount = 0,
                        Login = 1,
                        OnQueue = 2,
                        OnQueueAsViewer = 3,
                        LeaveQueue = 4,
                        Surrender = 5,
                        MyMove = 6,
                        PlayerWin = 7,
                        UseChatWheel = 8;
}

public static class ServerToClientSignifiers
{
    public const int LoginComplete = 0,
                        LoginFailed = 1,
                        AccountCreationComplete = 2,
                        AccountCreationFailed = 3,
                        WaitForOpponent = 4,
                        GameRoomCreated = 5,
                        YourTurn = 6,
                        UpdateForViewers = 7,
                        YouWin = 8,
                        YouLose = 9,
                        OpponentChatWheel = 10;
}