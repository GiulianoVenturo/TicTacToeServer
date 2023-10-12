using UnityEngine;
using UnityEngine.Assertions;
using Unity.Collections;
using Unity.Networking.Transport;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.IO;

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

        string u = csv[1];
        string p = csv[2];
        var filteredAccounts = playerAccounts.Where(account => account.Username == u);
        bool accountExists = filteredAccounts.Any();

        if (signifier == ClientToServerSignifiers.CreateAccount)
        {
            Debug.Log("Creating account...");
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
            if (accountExists)
            {
                PlayerAccount matchedAccount = filteredAccounts.FirstOrDefault();
                if (matchedAccount.Password == p)
                {
                    SendMessageToClient($"{ServerToClientSignifiers.LoginComplete}", networkConnections[connectionIndex]);
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

public static class ClientToServerSignifiers
{
    public const int CreateAccount = 0,
                        Login = 1;
}

public static class ServerToClientSignifiers
{
    public const int LoginComplete = 0,
                        LoginFailed = 1,
                        AccountCreationComplete = 2,
                        AccountCreationFailed = 3;
}