﻿using EI.SI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Projeto
{
    public partial class ChatForm : Form
    {

        bool mouseDown;
        private Point offset;

        private const int PORT = 10000;
        NetworkStream networkstream;
        ProtocolSI protocolSI;
        TcpClient tcpClient;
        RSACryptoServiceProvider RSA = new RSACryptoServiceProvider();
        UnicodeEncoding ByteConverter = new UnicodeEncoding();
        Thread trd = null;

        private string aeskey;
        private string aesiv;

        private bool sendingFile = false;

        // Faz com que seja possível esconder o painel do perfil clicando fora do mesmo (https://stackoverflow.com/questions/37093409/c-sharp-windowsforms-hide-control-after-clicking-outside-of-it)
        const int WM_PARENTNOTIFY = 0x210;
        const int WM_LBUTTONDOWN = 0x201;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_LBUTTONDOWN || (m.Msg == WM_PARENTNOTIFY &&
                (int)m.WParam == WM_LBUTTONDOWN))
                if (!profilePanel.ClientRectangle.Contains(
                                 profilePanel.PointToClient(Cursor.Position)))
                    profilePanel.Hide();
            base.WndProc(ref m);
        }


        public ChatForm(string username)
        {
            InitializeComponent();

            IPEndPoint endpoint = new IPEndPoint(IPAddress.Loopback, PORT);
            tcpClient = new TcpClient();
            tcpClient.Connect(endpoint);
            networkstream = tcpClient.GetStream();
            protocolSI = new ProtocolSI();

            string publickey = RSA.ToXmlString(false);

            byte[] packet = protocolSI.Make(ProtocolSICmdType.USER_OPTION_1, username);
            networkstream.Write(packet, 0, packet.Length);

            packet = protocolSI.Make(ProtocolSICmdType.PUBLIC_KEY, Encoding.ASCII.GetBytes(publickey));
            networkstream.Write(packet, 0, packet.Length);

            trd = new Thread(new ThreadStart(this.ReceiveMessagesThread));
            trd.IsBackground = true;
            trd.Start();           

            usernameLbl.Text = username;
            panelUsrname.Text = username;
        }

        //função para fechar o cliente e terminar sessão
        private void CloseClient()
        {
            try
            {
                byte[] eot = protocolSI.Make(ProtocolSICmdType.EOT);
                networkstream.Write(eot, 0, eot.Length);

                networkstream.Read(protocolSI.Buffer, 0, protocolSI.Buffer.Length);

                networkstream.Close();
                tcpClient.Close();
            }
            catch { }
        }

        private void sendFileBtn_Click(object sender, EventArgs e)
        {
            if(sendingFile == true) { return; }

            OpenFileDialog ofd = new OpenFileDialog();
            
            if(ofd.ShowDialog() == DialogResult.OK)
            {
                sendingFile = true;

                byte[] packet;

                string filePath = ofd.FileName;
                string fileName = ofd.SafeFileName;
                
                var fileBytes = File.ReadAllBytes(filePath);

                packet = protocolSI.Make(ProtocolSICmdType.USER_OPTION_1, fileName);
                networkstream.Write(packet, 0, packet.Length);

                trd = new Thread(() => SendDataThread(ProtocolSICmdType.USER_OPTION_2 ,fileBytes));
                trd.IsBackground = true;
                trd.Start();                                       
            }
        }

        delegate void SetTextCallback(string text);
        private void SetText(string text)
        {
            // InvokeRequired required compares the thread ID of the
            // calling thread to the thread ID of the creating thread.
            // If these threads are different, it returns true.
            try
            {
                if (this.chat.InvokeRequired)
                {
                    SetTextCallback d = new SetTextCallback(SetText);
                    this.Invoke(d, new object[] { text });
                }
                else
                {
                    chat.Items.Add(text);
                }
            }
            catch (Exception)
            {
            }          
        }

        delegate void SetFileCallback(FileMessage fileMessage);
        private void SetFile(FileMessage fileMessage)
        {
            // InvokeRequired required compares the thread ID of the
            // calling thread to the thread ID of the creating thread.
            // If these threads are different, it returns true.
            try
            {
                if (this.chat.InvokeRequired)
                {
                    SetFileCallback d = new SetFileCallback(SetFile);
                    this.Invoke(d, new object[] { fileMessage });
                }
                else
                {
                    sendingFile = false;
                    chat.Items.Add(fileMessage);
                }
            }
            catch (Exception) { }
        }

        delegate void changeOnlineUsersCallback(string text);
        private void changeOnlineUsers(string text)
        {
            // InvokeRequired required compares the thread ID of the
            // calling thread to the thread ID of the creating thread.
            // If these threads are different, it returns true.
            try
            {
                if (this.onlineUsersLbl.InvokeRequired)
                {
                    changeOnlineUsersCallback d = new changeOnlineUsersCallback(changeOnlineUsers);
                    this.Invoke(d, new object[] { text });
                }
                else
                {
                    onlineUsersLbl.Text = text;
                }
            }
            catch (Exception) { }
        }

        private void ReceiveMessagesThread()
        {
            byte[] finalDataBytes = new byte[] { };
            byte[] finalFileBytes = new byte[] { };
            int dataLength = 0;
            int fileLength = 0;

            byte[] DecryptedMsg;
            byte[] ack;

            while (tcpClient.Connected)
            {
                try
                {
                    networkstream.Read(protocolSI.Buffer, 0, protocolSI.Buffer.Length);
                    byte[] dataBytes;

                    switch (protocolSI.GetCmdType())
                    {
                        case ProtocolSICmdType.DATA:
                            dataBytes = protocolSI.GetData();

                            finalDataBytes = finalDataBytes.Concat(dataBytes).ToArray();

                            if (finalDataBytes.Length != dataLength) { break; }

                            DecryptedMsg = AesDecrypt(finalDataBytes);

                            string msg = Encoding.UTF8.GetString(DecryptedMsg);

                            SetText(msg);

                            finalDataBytes = new byte[] { };
                            dataLength = 0;
                            break;

                        case ProtocolSICmdType.USER_OPTION_2:
                            dataBytes = protocolSI.GetData();

                            finalFileBytes = finalFileBytes.Concat(dataBytes).ToArray();

                            if (finalDataBytes.Length != fileLength) { break; }
                            
                            HandleFileMessage(finalFileBytes);
                            finalFileBytes = new byte[] { };
                            fileLength = 0;
                            break;
                       
                        case ProtocolSICmdType.USER_OPTION_3:
                            dataLength = Int32.Parse(protocolSI.GetStringFromData());
                            break;

                        case ProtocolSICmdType.USER_OPTION_4:
                            string pod = protocolSI.GetStringFromData();
                            changeOnlineUsers(pod);
                            break;

                        case ProtocolSICmdType.SECRET_KEY:
                             dataBytes = protocolSI.GetData();
                             byte[] key = RSA.Decrypt(dataBytes, false);
                             aeskey = ByteConverter.GetString(key);

                             ack = protocolSI.Make(ProtocolSICmdType.ACK);
                             networkstream.Write(ack, 0, ack.Length);
                             break;

                        case ProtocolSICmdType.IV:
                            dataBytes = protocolSI.GetData();
                            byte[] iv = RSA.Decrypt(dataBytes, false);
                            aesiv = ByteConverter.GetString(iv);

                            ack = protocolSI.Make(ProtocolSICmdType.ACK);
                            networkstream.Write(ack, 0, ack.Length);
                            break;
                        
                    }
                }
                catch (Exception) { }
            }
        }

        public byte[] PackMessage(byte [] msg)
        {
            byte[] signature = SignData(msg);
            byte[] encMsg = AesEncryption(msg);

            ArrayM arrayM = new ArrayM() { message = encMsg, signatureHash = signature};
            string mensagem = System.Text.Json.JsonSerializer.Serialize(arrayM);

            return Encoding.UTF8.GetBytes(mensagem);

        }

        public byte[] AesEncryption(byte[] data)
        {
            MemoryStream ms = new MemoryStream();
            AesCryptoServiceProvider aes = new AesCryptoServiceProvider();

            aes.IV = Convert.FromBase64String(aesiv);
            aes.Key = Convert.FromBase64String(aeskey);

            CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(aes.Key, aes.IV), CryptoStreamMode.Write);

            cs.Write(data, 0, data.Length);
            cs.FlushFinalBlock();

            data = ms.ToArray();

            ms.Flush();
            cs.Close();
            ms.Close();


            return data;
        }

        public byte[] AesDecrypt(byte[] data)
        {
            MemoryStream ms = new MemoryStream();
            AesCryptoServiceProvider aes = new AesCryptoServiceProvider();

            aes.IV = Convert.FromBase64String(aesiv);
            aes.Key = Convert.FromBase64String(aeskey);


            CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(aes.Key, aes.IV), CryptoStreamMode.Write);

            cs.Write(data, 0, data.Length);
            cs.FlushFinalBlock();

            byte[] msgbytes = ms.ToArray();

            ms.Flush();
            cs.Close();
            ms.Close();

            return msgbytes;
        }

        private void SendDataThread(ProtocolSICmdType type, byte[] data)
        {
            byte[] mensagem = PackMessage(data);
            byte[] packet;

            Info info = new Info();
            info.tamanho = mensagem.Length;
            info.tipo = type.ToString();

            string infos = System.Text.Json.JsonSerializer.Serialize(info);

            packet = protocolSI.Make(ProtocolSICmdType.USER_OPTION_3, infos);
            networkstream.Write(packet, 0, packet.Length);

            do
            {
                if (data.Length >= 1400)
                {
                    packet = protocolSI.Make(type, mensagem.Take(1400).ToArray());
                }
                else
                {
                    packet = protocolSI.Make(type, mensagem);
                }

                mensagem = mensagem.Skip(1400).ToArray();

                networkstream.Write(packet, 0, packet.Length);
            }
            while (mensagem.Length > 0);
        }

        private void HandleFileMessage(byte[] file)
        {
            BinaryFormatter formatter = new BinaryFormatter();
            MemoryStream ms = new MemoryStream();
            ASCIIEncoding ascii = new ASCIIEncoding();
            ms.Write(file, 0, file.Length);
            ms.Position = 0;
            
            List<byte[]> fileInformation = formatter.Deserialize(ms) as List<byte[]>;
            FileMessage fileMessage = new FileMessage(ascii.GetString(fileInformation[0], 0, fileInformation[0].Length), fileInformation[1], ascii.GetString(fileInformation[2], 0, fileInformation[2].Length));
            ms.Flush();
            ms.Close();
            
            SetFile(fileMessage);
        }

        private void chat_SelectedIndexChanged(object sender, EventArgs e)
        {
            if(chat.SelectedItem == null || !chat.SelectedItem.GetType().ToString().Contains("FileMessage")) { return; }

            FileMessage file = (FileMessage)chat.SelectedItem;

            SaveFileDialog saveFileDialog = new SaveFileDialog();

            saveFileDialog.FileName = file.fileName;
            saveFileDialog.Filter = $"(*{Path.GetExtension(file.fileName)}) | *{Path.GetExtension(file.fileName)}";
            saveFileDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            if(saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                File.WriteAllBytes(Path.GetFullPath(saveFileDialog.FileName), file.fileBytes);
            }

            chat.ClearSelected();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            CloseClient();
        }

        private void dockPanel_MouseDown(object sender, MouseEventArgs e)
        {
            offset.X = e.X;
            offset.Y = e.Y;
            mouseDown = true;
        }

        private void dockPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if(mouseDown == true)
            {
                Point currentScreenPos = PointToScreen(e.Location);
                Location = new Point(currentScreenPos.X - offset.X, currentScreenPos.Y - offset.Y);
            }
        }

        private void dockPanel_MouseUp(object sender, MouseEventArgs e)
        {
            mouseDown = false;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            textBoxMsg.Select();
            this.ActiveControl = textBoxMsg;
            textBoxMsg.Focus();          
        }

        private void btnSend_Click(object sender, EventArgs e)
        {
            string msg = textBoxMsg.Text;

            if (msg.Trim() == "") { return; }
            if (msg.Length > 100) { MessageBox.Show($"O tamanho da mensagem excede o máximo de 100 caractéres!{Environment.NewLine}Tamanho da mensagem: {msg.Length} caractéres.", "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            //converter a msg num pacote
            textBoxMsg.Clear();

            SetText($"{DateTime.Now.ToString("HH:mm")} | {usernameLbl.Text}: {msg}");

            byte[] bytes = Encoding.UTF8.GetBytes(msg);

            trd = new Thread(() => SendDataThread(ProtocolSICmdType.DATA, bytes));
            trd.IsBackground = true;
            trd.Start();
        }

        private byte[] SignData(byte[] msg)
        {
            using (SHA1 sha1 = SHA1.Create())
            {
                byte[] signature = RSA.SignData(msg, sha1);
                return signature;
            }
        }

        private void textBoxMsg_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
               btnSend_Click(sender, e);            
            }
        }

        private void btnClose_Click(object sender, EventArgs e)
        {
            DialogResult result = MessageBox.Show("Pretende sair do programa?", "Confirmação", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (result == DialogResult.Yes)
            {
                Application.Exit();
            }
        }

        private void btnMinimize_Click(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
        }

        private void avatarPB_Click(object sender, EventArgs e)
        {
            profilePanel.Visible = true;
        }

        private void btnLogOut_Click(object sender, EventArgs e)
        {
            DialogResult result = MessageBox.Show("Pretende dar LogOut do Chat?", "Confirmação", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            
            if (result == DialogResult.Yes)
            {
                trd.Abort();
                LoginForm form = new LoginForm();
                form.Show();
                Close();
            }
        }
    }

    class FileMessage
    {
        public string fileName { get; }
        public byte[] fileBytes { get; }
        public string message;

        public FileMessage(string fileName, byte[] fileBytes, string message)
        {
            this.fileName = fileName;
            this.fileBytes = fileBytes;
            this.message = message;
        }

        public override string ToString()
        {
            return this.message;
        }
    }

    class ArrayM
    {
        public byte[] message { get; set; }
        public byte[] signatureHash { get; set; }
    }

    class Info
    {
        public int tamanho { get; set; }
        public string tipo { get; set; }
    }
}
