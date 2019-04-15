﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace WriteLogDigiRite
{
    public enum ExchangeTypes { GRID_SQUARE, DB_REPORT, ARRL_FIELD_DAY, ARRL_RTTY, GRID_SQUARE_PLUS_REPORT};
    
    public partial class MainForm : Form, QsoQueue.IQsoQueueCallBacks, Qso2MessageExchange.IQsoQueueCallBacks
    {
        public static String[] DefaultAcknowledgements = { "73", "RR73", "RRR" };

        // These are the objects needed to receive and send FT8.
        private XDft.Demodulator demodulator;
        private XDft.WsjtSharedMemory wsjtSharedMem;
        private XDft.WsjtExe wsjtExe;
        private XD.WaveDevicePlayer waveDevicePlayer;
        private XD.WaveDeviceTx deviceTx = null; // to modulate

        private XcvrForm rxForm;
        private String myCall;
        private String myBaseCall; // in case myCall is nonstandard
        private String myGrid;
        private int instanceNumber;
        private String instanceRegKeyName;

        private uint RxInDevice = 0;
        private uint TxOutDevice = 0;
        private bool SetupMaySelectDevices = true;
        private bool SetupMaySelectLR = true;

        private IQsoQueue qsoQueue;
        private LogFile logFile;
        private LogFile conversationLogFile;
        private bool sendInProgress = false;

        // what we put in listToMe and cqlist
        private CallPresentation cqListOdd;
        private CallPresentation cqListEven;
        private CallPresentation toMe;
        private QsosPanel qsosPanel;

        private bool controlVFOsplit = false;
        private bool forceRigUsb = false;
        private int TxHighFreqLimit = 0;

#if DEBUG
        List<string> simulatorLines;
        int simulatorTimeOrigin = -1;
        DateTime simulatorStart;
        int simulatorNext = 0;
#endif

        public MainForm(int instanceNumber)
        {
            this.instanceNumber = instanceNumber;
            instanceRegKeyName = String.Format("Software\\W5XD\\WriteLog\\DigiRite-{0}", instanceNumber);
            InitializeComponent();
            labelPtt.Text = "";
        }

        #region WriteLog automation
        // unused if WriteLog is not present
        private WriteLogClrTypes.ISingleEntry iWlEntry = null;
        private WriteLogClrTypes.ISingleEntry iWlDupingEntry = null;
        private short currentBand = 0;
        private WriteLogClrTypes.IWriteL iWlDoc = null;
        private System.IO.Ports.SerialPort pttPort = null;

        public void SetWlEntry(object wl)
        {
            labelPtt.Text = "";
            if (pttPort != null)
                pttPort.Dispose();
            pttPort = null;
            if (wl == null)
            {
                iWlEntry = null;
                iWlDoc = null;
                iWlDupingEntry = null;
            }
            else
            {
                iWlEntry = (WriteLogClrTypes.ISingleEntry)(wl);
                iWlDoc = (WriteLogClrTypes.IWriteL)iWlEntry.GetParent();
                iWlDupingEntry = iWlDoc.CreateEntry();
                SetupExchangeFieldNumbers();
                string RttyRegKeyName ="Software\\W5XD\\writelog.ini\\RttyRite";
                if (instanceNumber > 1)
                    RttyRegKeyName += instanceNumber.ToString();
                Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RttyRegKeyName);
                if (null != rk)
                {
                    object Port = rk.GetValue("Port");
                    if (null != Port)
                    {
                        int port;
                        if (Int32.TryParse(Port.ToString(), out port) && port > 0)
                        {
                            try
                            {
                                string portname = "COM" + port.ToString();
                                pttPort = new System.IO.Ports.SerialPort(portname);
                                pttPort.Handshake = System.IO.Ports.Handshake.None;
                                pttPort.RtsEnable = false;
                                pttPort.Open();
                                labelPtt.Text = "ptt on " + portname;
                            }
                            catch (System.Exception )
                            { 
                                pttPort = null;
                            }
                        }
                    }
                }
            }
        }

        private int SentRstFieldNumber = -1;
        private int ReceivedRstFieldNumber = -1;
        private int GridSquareReceivedFieldNumber = -1;
        private int GridSquareSentFieldNumber = -1;
        private int DgtlFieldNumber = -1;

        private void SetupExchangeFieldNumbers()
        {
            if (null == iWlDoc)
                return;
            WriteLogClrTypes.IQsoCollection qsoc = iWlDoc.GetQsoCollection() as WriteLogClrTypes.IQsoCollection;
            string[] names = qsoc.GetColumnAdifNames();
            int appWriteLogGrid = -1;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == "RST_SENT")
                    SentRstFieldNumber = i + 1;
                else if (names[i] == "RST_RCVD")
                    ReceivedRstFieldNumber = i + 1;
                else if (names[i] == "GRIDSQUARE")
                    GridSquareReceivedFieldNumber = i + 1;
                else if (names[i] == "APP_WRITELOG_MYGRID")
                    GridSquareSentFieldNumber = i + 1;
                else if (names[i] == "APP_WRITELOG_GRID")
                    appWriteLogGrid = i + 1;
            }
            if (GridSquareReceivedFieldNumber <= 0)
                GridSquareReceivedFieldNumber = appWriteLogGrid;
            string []titles = qsoc.GetColumnTitles();
            for (int i = 0; i < titles.Length; i++)
            {
                if (titles[i].ToUpper().IndexOf("DGTL") >= 0)
                {
                    DgtlFieldNumber = i + 1;
                    break;
                }
            }
#if DEBUG
            if (DgtlFieldNumber > 0)
                iWlEntry.SetFieldN((short)DgtlFieldNumber, "FT8");
#endif
        }
        #endregion

        public static uint StringToIndex(string MySetting, List<string> available)
        {
            uint ret = 0;
            string cmp = MySetting.ToUpper();
            for (ret = 0; ret < available.Count; ret++)
            {
                if (available[(int)ret].ToUpper().Contains(cmp))
                    break;
            }
            return ret;
        }

        private string LogFilePath { get { return wsjtExe.AppDirectoryPath + "DigiRite.log"; } }

        const double AUDIO_SLIDER_SCALE = 8;
        private bool InitSoundInAndOut()
        {
            // The objects implement IDisposable. Failing to
            // dispose of one after quitting using it 
            // leaves its Windows resources
            // allocated until garbage collection.
            rxForm.demodParams = null;
            timerFt8Clock.Enabled = false;
            timerSpectrum.Enabled = false;
            if (null != demodulator)
                demodulator.Dispose();
            demodulator = null;
            if (null != wsjtSharedMem)
                wsjtSharedMem.Dispose();
            wsjtSharedMem = null;
            if (null != wsjtExe)
                wsjtExe.Dispose();
            wsjtExe = null;
            if (null != waveDevicePlayer)
                waveDevicePlayer.Dispose();
            waveDevicePlayer = null;
            if (null != deviceTx)
                deviceTx.Dispose();
            deviceTx = null;
            if (null != logFile)
                logFile.Dispose();
            logFile = null;
            if (null != conversationLogFile)
                conversationLogFile.Dispose();
            conversationLogFile = null;

            // The demodulator invokes the wsjtx decoder
            demodulator = new XDft.Demodulator();
            // the names of its parameters are verbatim from the wsjt-x source code.
            // Don't ask this author what they mean.
            demodulator.nftx = 1500;
            demodulator.nfqso = 1500;
            demodulator.nfa = 200;
            demodulator.nfb = 6000;

            if (Properties.Settings.Default.Decode_ndepth < 1)
                Properties.Settings.Default.Decode_ndepth = 1;
            if (Properties.Settings.Default.Decode_ndepth > 3)
                Properties.Settings.Default.Decode_ndepth = 1;
            demodulator.ndepth = Properties.Settings.Default.Decode_ndepth;
            demodulator.lft8apon = Properties.Settings.Default.Decode_lft8apon;

            // When the decoder finds an FT8 message, it calls us back...
            // ...on a foreign thread. Call BeginInvoke to get back on this one. See below.
            demodulator.DemodulatorResultCallback = new XDft.DemodResult(Decoded);

            string sharedMemoryKey = "DigiRite-" + instanceNumber.ToString();
            wsjtSharedMem = new XDft.WsjtSharedMemory(sharedMemoryKey, false);
            if (!wsjtSharedMem.CreateWsjtSharedMem())
            {
                MessageBox.Show("Failed to create Shared Memory from " + sharedMemoryKey);
                return false;
            }

            // The subprocess itself is managed by the XDft
            wsjtExe = new XDft.WsjtExe();
            wsjtExe.AppDataName = "DigiRite-" + instanceNumber.ToString();

            if (!wsjtExe.CreateWsjtProcess(wsjtSharedMem))
            {
                MessageBox.Show("Failed to launch wsjt exe");
                demodulator.Dispose();
                wsjtExe.Dispose();
                wsjtExe = null;
                wsjtSharedMem.Dispose();
                wsjtSharedMem = null;
                demodulator = null;
                return false;
            }

            logFile = new LogFile(LogFilePath);
            String conversationLog = wsjtExe.AppDirectoryPath + "Conversation.log";
            conversationLogFile = new LogFile(conversationLog, false);

            rxForm.logFile = logFile;

            uint channel = (uint)Properties.Settings.Default["AudioInputChannel_" + instanceNumber.ToString()];
            if (waveDevicePlayer != null)
                waveDevicePlayer.Dispose();
            waveDevicePlayer = new XD.WaveDevicePlayer();
            if (!waveDevicePlayer.Open(RxInDevice, channel, demodulator.GetRealTimeRxSink()))
            {
                MessageBox.Show("Failed to open wave input");
                waveDevicePlayer.Dispose();
                waveDevicePlayer = null;
                return false;
            }
            else
            {
                rxForm.demodParams = demodulator;
                waveDevicePlayer.Resume();
                rxForm.Player = waveDevicePlayer;
            }

            deviceTx = new XD.WaveDeviceTx();
            channel = (uint)Properties.Settings.Default["AudioOutputChannel_" + instanceNumber.ToString()];
            if (!deviceTx.Open(TxOutDevice, channel))
            {
                MessageBox.Show("Failed to open wave output");
                deviceTx.Dispose();
                deviceTx = null;
                return false;
            }
            deviceTx.SoundSyncCallback = new XD.SoundBeginEnd(AudioBeginEnd);
            float gain = deviceTx.Gain;
            bool gainOK = gain >= 0;
            if (gainOK)
            {   // not sure why the windows volume slider don't
                // really work with linear commands, but here we go:
                double g = trackBarTxGain.Maximum + Math.Log(gain) * AUDIO_SLIDER_SCALE / Math.Log(2);
                int v = (int)g;
                if (v < trackBarTxGain.Minimum)
                    v = trackBarTxGain.Minimum;
                trackBarTxGain.Value = v;
            }
            trackBarTxGain.Enabled = gainOK;
            timerFt8Clock.Enabled = true;
            timerSpectrum.Enabled = true;
            return true;
        }

#region received message interactions

        private List<XDpack77.Pack77Message.ReceivedMessage> recentMessages = 
            new List<XDpack77.Pack77Message.ReceivedMessage>();

        private DateTime watchDogTime; // the dog sleeps only for so long
        private const int MAX_NUMBER_OF_FT8_CHARS = 37; // truncate decoder strings

        private void OnReceived(String s, int cycle)
        {   // When the FT8 decoder is invoked, it may find 
            // multiple signals in the stream. Each is notified by
            // a separate string here. An empty string is sent
            // at the end of the decoding session.
            if (!String.IsNullOrEmpty(s))
            {
                OneAtATime(new OneAtATimeDel(() =>
                {
                    int v = s.IndexOf("~  ");
                    // "020000  -9  0.4  500 ~  CQ RU W5XD EM10                         "
                    if (v >= 0)
                    {
                        logFile.SendToLog(s);
                        string msg = s.Substring(v + 3);
                        if (msg.Length > MAX_NUMBER_OF_FT8_CHARS)
                            msg = msg.Substring(0, MAX_NUMBER_OF_FT8_CHARS);
                        int i3 = 0; int n3 = 0;
                        bool[] c77 = null;
                        XDft.Generator.pack77(msg, ref i3, ref n3, ref c77);
                        // kludge...see if pack failed cuz of hashed call
                        // This works around a bug in wsjtx's pack77 routine.
                        // If/when that bug is fixed, this code may be removed
                        if ((i3 == 0) && (n3 == 0))
                        {   // free text...see if removing <> makes it parse
                            string changedMessage = msg;
                            bool changed = false;
                            for (; ; )
                            {
                                int idx = changedMessage.IndexOfAny(new char[] { '<', '>' });
                                if (idx >= 0)
                                {
                                    changed = true;
                                    changedMessage = changedMessage.Substring(0, idx) 
                                        + changedMessage.Substring(idx + 1);
                                }
                                else
                                    break;
                            }
                            if (changed)
                                XDft.Generator.pack77(changedMessage, ref i3, ref n3, ref c77);
                            // END kludge.
                        }
                        // have a look at the packing type. i3 and n3
                        XDpack77.Pack77Message.ReceivedMessage rm =
                            XDpack77.Pack77Message.ReceivedMessage.CreateFromReceived(i3, n3, s.Substring(0, v), msg, cycle);
                        if (rm == null)
                            return; // FIXME. some messages we can't parse

                        // recentMessages retains only matching TimeTag's
                        if (recentMessages.Any() && recentMessages.First().TimeTag != rm.TimeTag)
                            recentMessages.Clear();

                        // discard message decodes that we already have
                        foreach (var m in recentMessages)
                            if (m.Match(rm)) return;

                        rxForm.OnReceived(rm);
                        recentMessages.Add(rm);

                        // certain kinds of messages are promoted to the checkbox lists
                        XDpack77.Pack77Message.ToFromCall toFromCall = rm.Pack77Message as XDpack77.Pack77Message.ToFromCall;
                        String toCall = toFromCall?.ToCall;
                        bool directlyToMe = (toCall != null) && ((toCall == myCall) || (toCall == myBaseCall));

                        if (directlyToMe)
                            watchDogTime = DateTime.UtcNow;

                        short mult = 0;
                        int dupe = 0;
                        String fromCall = toFromCall?.FromCall;
                        RecentMessage recentMessage; 
                        if (fromCall != null && null != iWlDupingEntry)
                        {   // dupe check if we can
                            iWlDupingEntry.ClearEntry();
                            if (DgtlFieldNumber > 0)
                                iWlDupingEntry.SetFieldN((short)DgtlFieldNumber, "FT8");
                            iWlDupingEntry.Callsign = fromCall;
                            dupe = iWlDupingEntry.Dupe();
                            if (dupe == 0)
                                mult = iWlDupingEntry.IsNewMultiplier((short)-1);
                        }
                        recentMessage = new RecentMessage(rm, dupe != 0, mult != 0);

                        bool isConversation = false;
                        string callQsled = (rm.Pack77Message as XDpack77.Pack77Message.QSL)?.CallQSLed;
                        if (!String.IsNullOrEmpty(toCall))
                            qsoQueue.MessageForMycall(recentMessage, directlyToMe,
                                    callQsled, currentBand,
                                    checkBoxRespondAny.Checked && dupe == 0,
                                    new IsConversationMessage((origin) =>
                                        {   // qsoQueue liked this message. log it
                                            isConversation = true;
                                            string toLog = s.Substring(0,v) + msg;
                                            listBoxConversation.Items.Add(new ListBoxConversationItem(toLog, origin));
                                            conversationLogFile.SendToLog(toLog);
                                            ScrollListBoxToBottom(listBoxConversation);
                                        }));
                        if (!isConversation)
                        {   // nobody above claimed this message
                            if (directlyToMe)
                                toMe.Add(new RecentMessage(rm, dupe != 0, mult != 0));
                            else if ((fromCall != null) &&
                                !String.Equals(fromCall, myCall) &&  // decoder is hearing our own
                                !String.Equals(fromCall, myBaseCall) &&  // transmissions
                                String.Equals("ALL", callQsled))
                            {
                                CallPresentation cqList = (cycle & 1) == 0 ? cqListEven : cqListOdd;
                                cqList.Add(recentMessage);
                            }
                        }
                    }
                }));
            }
        }

        private void ScrollListBoxToBottom(ListBox lb)
        {
            int visibleItems = lb.ClientSize.Height / lb.ItemHeight;
            lb.TopIndex = Math.Max(1 + lb.Items.Count - visibleItems , 0);
        }
        
#endregion

#region transmit management

        private int MAX_MESSAGES_PER_CYCLE {
            get {  return (int)numericUpDownStreams.Value; }
        }
        // empirically determined to "center" in the time slot
        private const int TX_AFTER_ZERO_MSEC = 550;

        private void AfterNmsec(Action d, int msec)
        {
            var timer = new Timer { Interval = msec };
            timer.Tick += new EventHandler((o,e) =>
                {
                    timer.Enabled = false;
                    d();
                    timer.Dispose();
                });
            timer.Enabled = true;
        }

        private const int FT8_SEC = 15;
        private bool[] transmittedForQSOLastCycle = new bool[2];
        private void transmitAtZero(bool allowLate = false)
        {   // right now we're at zero second in the cycle.
            DateTime toSend = DateTime.UtcNow;
            int nowSecond = toSend.Second;
            int cyclePos = nowSecond % FT8_SEC; // 0 through 14
            bool nowOdd = ((nowSecond / FT8_SEC) & 1) != 0;
            int seconds = toSend.Second;
            seconds /= FT8_SEC;
            seconds *= FT8_SEC; // round back to nearest 15
            int lastCycleIndex = nowOdd ? 0 : 1;
             // can't transmit two consecutive cycles, one odd and one even
             bool onUserSelectedCycle = nowOdd == radioButtonOdd.Checked;
            if ((transmittedForQSOLastCycle[lastCycleIndex])
                    && !onUserSelectedCycle)
                return;
            List<QueuedToSendListItem> toSendList = new List<QueuedToSendListItem>();
            // scan the checkboxes and decide what to send
            if (checkBoxManualEntry.Checked && onUserSelectedCycle)
            {   // the manual entry is not associated with a QSO
                String ts = textBoxMessageEdit.Text.ToUpper();
                if (!String.IsNullOrEmpty(ts))
                {   // if there is typed in text, send it
                    checkBoxManualEntry.Checked = false;
                    toSendList.Add(new QueuedToSendListItem(ts, null));
                }
            }

            for (int i = 0; i < listBoxAlternatives.Items.Count; i++)
            {   // alternative messages are next on priority list after manual
                if (toSendList.Count >= MAX_MESSAGES_PER_CYCLE)
                    break;
                if (listBoxAlternatives.GetItemChecked(i))
                {
                    QueuedToSendListItem li = listBoxAlternatives.Items[i] as QueuedToSendListItem;
                    bool sendOdd = ((li.q.Message.CycleNumber + 1) & 1) != 0;
                    if (sendOdd == nowOdd)
                    {
                        toSendList.Add(li);
                        listBoxAlternatives.SetItemChecked(i, false);
                    }
                    break;
                }
            }

            // double list search is to maintain the priority order in listBoxInProgress.
            // the order things appear in checkedListBoxToSend is irrelevant.
            List<QueuedToSendListItem> inToSend = new List<QueuedToSendListItem>();
            var inProgress = qsosPanel.QsosInProgress;
            for (int i = 0; i < inProgress.Count; i++)
            {   // first entries are highest priority
                QsoInProgress q = inProgress[i];
                if (null == q)
                    continue;   // manual entry goes this way
                if (!q.Active)
                    continue;
                bool sendOdd = ((q.Message.CycleNumber + 1) & 1) != 0;
                if (sendOdd != nowOdd)
                    continue; // can't send this one cuz we're on wrong cycle
                for (int j = 0; j < checkedlbNextToSend.Items.Count; j++)
                {
                    if (!checkedlbNextToSend.GetItemChecked(j))
                        continue;   // present, but marked to skip
                    QueuedToSendListItem qli = checkedlbNextToSend.Items[j] as QueuedToSendListItem;
                    if ((null != qli)&& Object.ReferenceEquals(qli.q, q))
                        inToSend.Add(qli);
                }
            }

            foreach (QueuedToSendListItem qli in inToSend)
            {
                if (toSendList.Count >= MAX_MESSAGES_PER_CYCLE)
                    break;
                checkedlbNextToSend.Items.Remove(qli);
                if (toSendList.Any((qalready) => { 
                    if (null != qalready)
                        return String.Equals(qli.q.HisCall, qalready.q.HisCall);
                    return false;}
                    ))
                    continue; // already a send to this callsign. don't allow another
                toSendList.Add(qli);
            }
    
            bool anyToSend = toSendList.Any();
            int thisCycleIndex = nowOdd ? 1 : 0;
            transmittedForQSOLastCycle[thisCycleIndex] = anyToSend;

            if (!anyToSend && checkBoxCQ.Checked && onUserSelectedCycle)
            {   // only CQ if we have nothing else to send
                string cq = "CQ";
                /* 77-bit pack is special w.r.t. CQ. can't sent directed CQ 
                ** with call that won't fit in 28 of those bits. */
                bool fullcallok = (myBaseCall == myCall);
                if ((null != iWlDoc) && fullcallok)
                {   // get CQ message from WriteLog if its there
                    const short WRITELOG_CQ_MESSAGE_NUMBER = 9;
                    var split = iWlDoc.GetFKeyMsgDigital(
                        WRITELOG_CQ_MESSAGE_NUMBER).Split((char[])null);
                    if ((split.Length >= 2) && (split[0].ToUpper() == "CQ"))
                    {
                        if ((split[1].Length <= 4) && split[1].All(Char.IsLetter))
                        {
                            string nextword = split[1].ToUpper();
                            cq += " " + nextword;
                        }
                    }
                }
                cq += " " + myCall;
                if (fullcallok)
                    cq += " " + myGrid;
                toSendList.Add(new QueuedToSendListItem(cq, null));
                if (!checkBoxAutoXmit.Checked)
                    checkBoxCQ.Checked = false;
            }

            List<XDft.Tone> itonesToSend = new List<XDft.Tone>();
            List<int> freqsUsed = new List<int>();
            const int freqRange = 61;
            int freqIncrement = freqRange+1;
            foreach (var item in toSendList)
            {
                QsoInProgress q = item.q;
                int freq = TxFrequency;
                if (null != q)
                {
                    uint assigned = q.TransmitFrequency;
                    if (assigned != 0)
                        freq = (int)assigned;
                }

                // prohibit overlapping send frequencies
                for (int i = 0; i < freqsUsed.Count; )
                {
                    int f = freqsUsed[i];
                    if ((freq <= f + freqRange) && (freq >= f - freqRange))
                    {   // overlaps
                        freq += freqIncrement;
                        i = 0;
                        if (freqIncrement > 0)
                            freqIncrement = -freqIncrement;
                        else
                        {
                            freqIncrement = -freqIncrement;
                            freqIncrement += freqRange + 1;
                        }
                    }
                    else
                        i++;
                }
                if ((null != q) && q.TransmitFrequency != (uint)freq)
                {   // always transmit to this guy on one frequency
                    q.TransmitFrequency = (uint)freq;
                }
                freqsUsed.Add(freq);
                String asSent = null;
                int[] itones = null;
                bool[] ft8bits = null;
                XDft.Generator.genft8(item.MessageText, ref asSent, ref itones, ref ft8bits);
                const float RELATIVE_POWER_THIS_QSO = 1.0f;
                itonesToSend.Add(new XDft.Tone(itones, RELATIVE_POWER_THIS_QSO, freq));
                string conversationItem = String.Format("{2:00}{3:00}{4:00} transmit {1,4}    {0}",
                        asSent,
                        freq, toSend.Hour, toSend.Minute, seconds);
                listBoxConversation.Items.Add(new ListBoxConversationItem(conversationItem, Conversation.Origin.TRANSMIT));
                conversationLogFile.SendToLog(conversationItem);
                ScrollListBoxToBottom(listBoxConversation);
                logFile.SendToLog("TX: " + item.MessageText);
                asSent = asSent.Trim();
                if (asSent != item.MessageText)
                    logFile.SendToLog("TX error sent \"" + asSent + "\" instead of \"" + item.MessageText + "\"");
            }

            const int MAX_CONVERSATION_LISTBOX_ITEMS = 1000;
            if (listBoxConversation.Items.Count >= MAX_CONVERSATION_LISTBOX_ITEMS)
            {
                while (listBoxConversation.Items.Count >= MAX_CONVERSATION_LISTBOX_ITEMS - 100)
                    listBoxConversation.Items.RemoveAt(0);
                ScrollListBoxToBottom(listBoxConversation);
            }

            const int ALLOW_LATE_MSEC = 1800; // ft8 decoder only allows so much lateness.This is OK unless our clock is slow.

            if (itonesToSend.Any())
            {
                SetTxCycle(nowOdd ? 1 : 0);
                deviceTx.TransmitCycle = XD.Transmit_Cycle.PLAY_NOW;
                if (itonesToSend.Count == 1)
                {   // single set of tones is sent slightly differently than multiple
                    int[] itones = itonesToSend[0].itone;
                    if (allowLate)
                    {
                        if (cyclePos > 0)
                        {
                            int msecToTruncate = toSend.Millisecond + 1000 * cyclePos; // how late we are
                            msecToTruncate -= ALLOW_LATE_MSEC; // full itones don't last a full 15 seconds
                            int itonesToLose = msecToTruncate / 160;
                            if (itonesToLose > 0)
                            {
                                int[] truncated = new int[itones.Length - itonesToLose];
                                Array.Copy(itones, itonesToLose, truncated, 0, truncated.Length);
                                itones = truncated;
                            }
                        }
                    }
                    int freq = itonesToSend[0].frequency;
                    freq = RigVfoSplitForTx(freq, freq + 60);
                    sendInProgress = true;
                    AfterNmsec(new Action(() =>
                        XDft.Generator.Play(itones,
                            freq, deviceTx.GetRealTimeAudioSink())), TX_AFTER_ZERO_MSEC);
                }
                else 
                {   // multiple to send
                    int minFreq = 99999;
                    int maxFreq = 0;
                    foreach (var itones in itonesToSend)
                    {
                        if (itones.frequency > maxFreq)
                            maxFreq = itones.frequency;
                        if (itones.frequency < minFreq)
                            minFreq = itones.frequency;
                    }
                    int deltaFreq = 0;
                    if (minFreq < rxForm.MinDecodeFrequency)
                        deltaFreq = rxForm.MinDecodeFrequency - minFreq;
                    else if (maxFreq > rxForm.MaxDecodeFrequency + 60)
                        deltaFreq = rxForm.MaxDecodeFrequency + 60 - maxFreq; // negative
                    List<XDft.Tone> tones = new List<XDft.Tone>();
                    foreach (var itones in itonesToSend)
                    {
                        int[] nextTones = itones.itone;
                        if (allowLate)
                        {
                            if (cyclePos > 0)
                            {
                                int msecToTruncate = toSend.Millisecond + 1000 * cyclePos; // how late we are
                                msecToTruncate -= ALLOW_LATE_MSEC; // full itones don't last a full 15 seconds
                                int itonesToLose = msecToTruncate / 160;
                                if (itonesToLose > 0)
                                {
                                    int[] truncated = new int[nextTones.Length - itonesToLose];
                                    Array.Copy(nextTones, itonesToLose, truncated, 0, truncated.Length);
                                    nextTones = truncated;
                                }
                            }
                        }

                        XDft.Tone thisSignal = new XDft.Tone(nextTones, 1.0f, itones.frequency + deltaFreq);
                        tones.Add(thisSignal);
                    }
                    RigVfoSplitForTx(minFreq, maxFreq + 60, tones);
                    sendInProgress = true;
                    AfterNmsec(new Action(() =>
                        XDft.Generator.Play(tones.ToArray(), deviceTx.GetRealTimeAudioSink())), TX_AFTER_ZERO_MSEC);
                }
            }

            // clear out checkedlbNextToSend of anything from a QSO no longer in QSO(s) in Progress
            for (int j = 0; j < checkedlbNextToSend.Items.Count; )
            {
                QueuedToSendListItem qli = checkedlbNextToSend.Items[j] as QueuedToSendListItem;
                QsoInProgress qp;
                if (null != qli && (null != (qp = qli.q)) && inProgress.Any((q) => Object.ReferenceEquals(q, qp) && q.Active))
                {   // if the QSO remains active in progress, but still in this list, it didn't get sent,
                    // mark it unchecked so user can see that.
                    checkedlbNextToSend.SetItemChecked(j, false);
                    j += 1;
                }
                else
                    checkedlbNextToSend.Items.RemoveAt(j);
            }
        }

        private int RigVfoSplitForTx(int minAudioTx, int maxAudioTx, List<XDft.Tone> tones = null)
        {
            if (iWlEntry == null)
                return minAudioTx; // can't do rig control

            iWlEntry.SetTransmitFocus();

            short mode = 0;  short split = 0;double tx = 0;  double rx = 0;  // where is the rig now?
            iWlEntry.GetLogFrequency(ref mode, ref rx, ref tx, ref split);

            // want all outputs below TxHighFreqLimit
            // ...and, more importantly, above half that.
            int minFreq = TxHighFreqLimit / 2;
            int maxFreq = TxHighFreqLimit;

            int offset = 0;   // try to offset
            if (minAudioTx < minFreq)
            {
                offset = minFreq - minAudioTx; // positive. always
                offset /= 100;
                offset += 1;
                offset *= 100;
            }
            else if (maxAudioTx > maxFreq)
            {
                offset = maxFreq - maxAudioTx; // negative 
                offset /= 100;
                offset -= 1;
                offset *= 100;
            }

            // proposed split in offset
            if (((maxAudioTx - minAudioTx) >= (maxFreq - minFreq))
                || (offset == 0))
            {   //un-split the rig if the needed range is beyond
                // the setup parameters
                if (split != 0)
                    iWlEntry.SetLogFrequencyEx(mode, rx, rx, 0);
                return minAudioTx;
            }
 
            bool rigIsAlreadyOk = false;  // check if the rig has an acceptable split already
            if (split != 0)
            {
                int currentOffset = (int)(1000 * (rx - tx));
                if ((minAudioTx + currentOffset >= minFreq) &&
                    maxAudioTx + currentOffset <= maxFreq)
                {   // the rig's state is OK already
                    offset = currentOffset;
                    rigIsAlreadyOk = true;
                }
            }
            // offset is what we'll set
            if (!rigIsAlreadyOk)
                iWlEntry.SetLogFrequencyEx(mode, rx, rx - .001f * offset, 1);
            if (null != tones)
                foreach (var t in tones)
                    t.frequency += offset;
            return minAudioTx + offset;
        }
        
        private void SetTxCycle(int cycle)
        {
            if ((cycle & 1) == 0)
                radioButtonEven.Checked = true;
            else
                radioButtonOdd.Checked = true;
        }

        private void InitiateQsoFromMessage(RecentMessage rm, bool onHisFrequency)
        {
            if (!qsoQueue.InitiateQso(rm, currentBand, onHisFrequency, () =>
            {
                string s = rm.Message.ToString();
                // log the message
                listBoxConversation.Items.Add(new ListBoxConversationItem(s, Conversation.Origin.INITIATE));
                conversationLogFile.SendToLog(s);
                ScrollListBoxToBottom(listBoxConversation);
            }))
                return;

            RxFrequency = (int)rm.Message.Hz;
            watchDogTime = DateTime.UtcNow;
            if (!sendInProgress && (
                (rm.Message.CycleNumber & 1) != (cycleNumber & 1))
                && interval <= START_LATE_MESSAGES_THROUGH)
            {   // start late if we can
                transmitAtZero(true);
            }
        }

        const int MAX_UNANSWERED_MINUTES = 5;
        #endregion

        #region IQsoQueueCallBacks
        private const string BracketFormat = "<{0}>";
        private bool needBrackets(string call)
        {
            string baseCall = "";
            if (!XDft.Generator.checkCall(call, ref baseCall))
                return false; // should not happen
             return !String.Equals(baseCall, call);
        }

        public string GetExchangeMessage(QsoInProgress q, bool addAck)
        {
            var excSet = Properties.Settings.Default.ContestExchange;
            return GetExchangeMessage(q, addAck, (ExchangeTypes)excSet);
        }

        public string GetExchangeMessage(QsoInProgress q, bool addAck, ExchangeTypes excSet)
        {
            string hiscall = q.HisCall;
            bool hiscallNeedsBrackets = needBrackets(hiscall);
            string mycall = myCall;
            bool mycallNeedsBrackets = !String.Equals(myCall, myBaseCall);
            // 77-bit pack allows only one callsign to be bracketed.
            if (hiscallNeedsBrackets && mycallNeedsBrackets)
            {
                // reduce his call to his base cuz both cannot be hashed
                XDft.Generator.checkCall(hiscall, ref hiscall);
                mycall = String.Format(BracketFormat, myCall);
            }
            else if (hiscallNeedsBrackets)
                hiscall = String.Format(BracketFormat, hiscall);
            else if (mycallNeedsBrackets)
                mycall = String.Format(BracketFormat, mycall);

            if (null != iWlDoc)
            {
                // fill in from WriteLog if we can
                const short WRITELOG_EXCHANGE_MESSAGE_NUMBER = 0;
                string rawMessage = iWlDoc.GetFKeyMsgDigital(WRITELOG_EXCHANGE_MESSAGE_NUMBER);
                iWlEntry.Callsign = q.HisCall;
                if (q.SentSerialNumber == 0)
                {   // assign a serial number even if contest doesn't need it
                    iWlEntry.SerialNumber = 0; // get a fresh one
                    q.SentSerialNumber = iWlEntry.SerialNumber;
                }
                switch (excSet)
                {
                    case ExchangeTypes.ARRL_FIELD_DAY:
                        string entryclass = "";
                        var fdsplit = rawMessage.Split((char[])null,
                            StringSplitOptions.RemoveEmptyEntries);
                        bool founddigit = false;
                        foreach (string w in fdsplit)
                        {
                            if (!founddigit)
                            {
                                if (Char.IsDigit(w[0]))
                                {
                                    founddigit = true;
                                    entryclass = w;
                                }
                            }
                            else if (w.All(Char.IsLetter))
                                return String.Format("{0} {1} {2}{3} {4}", q.HisCall, myCall, 
                                    addAck ? "R " : "", entryclass, w);
                        }
                        break;

                    case ExchangeTypes.ARRL_RTTY:
                        string part = null;
                        String percentSearch = rawMessage;
                        String percentsRemoved = "";
                        for (; ; )
                        {   // is there a serial number in the message?
                            int percentPos = percentSearch.IndexOf('%');
                            if (percentPos < 0)
                            {
                                percentsRemoved += percentSearch;
                                break;  // no serial number
                            }
                            if ((percentPos < percentSearch.Length - 1) &&
                                Char.IsLetter(percentSearch[percentPos + 1]))
                            {
                                percentsRemoved += percentSearch.Substring(0, percentPos);
                                percentSearch = percentSearch.Substring(percentPos + 2);
                                continue; // this one is not a serial number
                            }
                            part = String.Format("{0:0000}", q.SentSerialNumber);
                            break;
                        }
                        if (String.IsNullOrEmpty(part))
                        {
                            var rttysplit = percentsRemoved.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                            foreach (string w in rttysplit)
                                if (w.All(Char.IsLetter))
                                {
                                    part = w.ToUpper();
                                    break;
                                }
                        }
                        return String.Format("{0} {1} {2}{3} {4}", q.HisCall, myCall, 
                            addAck ? "R " : "", q.Message.RST, part);

                    case ExchangeTypes.GRID_SQUARE:
                        if (GridSquareSentFieldNumber > 0)
                        {
                            string sentgrid = iWlEntry.GetFieldN((short)GridSquareSentFieldNumber).ToUpper();
                            if (sentgrid.Length >= 4)
                            {
                                q.SentGrid = sentgrid;
                                return String.Format("{0} {1} {2} {3}",
                                    hiscall, mycall, 
                                    addAck ? "R" : "" , sentgrid);
                            }
                        }
                        break;

                    case ExchangeTypes.DB_REPORT:
                        break; // handle below
                }
            }
            // if WriteLog is not running, or doesn't handle the exchange.
            switch (excSet)
            {
                case ExchangeTypes.DB_REPORT:
                    int dB = q.Message.SignalDB;
                    return String.Format("{0} {1} {2}{3:+00;-00;+00}",
                        hiscall,
                        mycall, 
                        addAck ? "R" : "", dB);
            }

            return String.Format("{0} {1} {2}{3}",
                hiscall,
                mycall, 
                addAck ? "R " : "", Properties.Settings.Default.MyGrid);
        }

        private string GetAckMessage(QsoInProgress q, bool ofAnAck, int whichAck)
        {
            // this one does non standard calls backwards from above. 
            // The standard call is the one that gets hashed.
            string hiscall = q.HisCall;
            bool hiscallNeedsBrackets = needBrackets(hiscall);
            string mycall = myCall;
            bool mycallNeedsBrackets = !String.Equals(myCall, myBaseCall);
            if (hiscallNeedsBrackets && mycallNeedsBrackets)
                hiscall = String.Format(BracketFormat, hiscall);
            else if (hiscallNeedsBrackets)
                mycall = String.Format(BracketFormat, myBaseCall);
            else if (mycallNeedsBrackets)
                hiscall = String.Format(BracketFormat, hiscall);
            return hiscall + " " + mycall + " " +
                DefaultAcknowledgements[whichAck];
        }

        public string GetAckMessage(QsoInProgress q, bool ofAnAck)
        {  return GetAckMessage(q, ofAnAck, q.AckMessage); }

        private void fillAlternativeMessages(QsoInProgress q)
        {
            listBoxAlternatives.Items.Clear();
            Dictionary<string, int> alreadyEntered = new Dictionary<string, int>();
            string msg = GetExchangeMessage(q, false);
            listBoxAlternatives.Items.Add(new QueuedToSendListItem(msg, q));
            alreadyEntered.Add(msg,0);
            int v;
            msg = GetExchangeMessage(q, true);
            if (!alreadyEntered.TryGetValue(msg, out v))
            {
                listBoxAlternatives.Items.Add(new QueuedToSendListItem(msg, q));
                alreadyEntered.Add(msg,0);
            }

            ExchangeTypes excSet = (ExchangeTypes)Properties.Settings.Default.ContestExchange;
            if (excSet != ExchangeTypes.DB_REPORT)
            {
                msg = GetExchangeMessage(q, false, ExchangeTypes.DB_REPORT);
                if (!alreadyEntered.TryGetValue(msg, out v))
                {
                    listBoxAlternatives.Items.Add(new QueuedToSendListItem(msg, q));
                    alreadyEntered.Add(msg,0);
                }
                msg = GetExchangeMessage(q, true, ExchangeTypes.DB_REPORT);
                if (!alreadyEntered.TryGetValue(msg, out v))
                {
                    listBoxAlternatives.Items.Add(new QueuedToSendListItem(msg, q));
                    alreadyEntered.Add(msg,0);
                }
            }
            if (excSet != ExchangeTypes.GRID_SQUARE)
            {
                msg = GetExchangeMessage(q, false, ExchangeTypes.GRID_SQUARE);
                if (!alreadyEntered.TryGetValue(msg, out v))
                {
                    listBoxAlternatives.Items.Add(new QueuedToSendListItem(msg, q));
                    alreadyEntered.Add(msg, 0);
                }
                msg = GetExchangeMessage(q, true, ExchangeTypes.GRID_SQUARE);
                if (!alreadyEntered.TryGetValue(msg, out v))
                {
                    listBoxAlternatives.Items.Add(new QueuedToSendListItem(msg, q));
                    alreadyEntered.Add(msg, 0);
                }
            }

            for (int i = 0; i < DefaultAcknowledgements.Length; i++)
            {
                msg = GetAckMessage(q, false, i);
                listBoxAlternatives.Items.Add(new QueuedToSendListItem(msg, q));
            }
        }

        public void SendMessage(string s, QsoInProgress q)
        {
            for (int i = 0; i < checkedlbNextToSend.Items.Count; i++)
            {   // if there is a message on this QSO already, remove it.
                QueuedToSendListItem qli = checkedlbNextToSend.Items[i] as QueuedToSendListItem;
                if (null != qli)
                {
                    if (Object.ReferenceEquals(qli.q, q))
                    {
                        checkedlbNextToSend.Items.RemoveAt(i);
                        break;
                    }
                }
            }
            if (q.Active)
            {
                int idx = checkedlbNextToSend.Items.Add(new SortedQueuedToSendListItem(s, q, qsosPanel));
                checkedlbNextToSend.SetItemChecked(idx, checkBoxAutoXmit.Checked && q.Active);
                checkedlbNextToSend.Sort();
            }
        }

        public void LogQso(QsoInProgress q)
        {
            if ((null != iWlEntry) && (null != iWlDoc))
            {
                iWlDupingEntry.ClearEntry();
                var excSet = Properties.Settings.Default.ContestExchange;
                {
                    short mode = 0;double tx = 0;  double rx = 0; short split = 0;
                    iWlEntry.GetLogFrequency(ref mode, ref rx, ref tx, ref split);
                    mode = 6;
                    iWlDupingEntry.SetLogFrequencyEx(mode, rx, tx, split);
                }
                // call, serial number,  RST and DGTL we set up front
                iWlDupingEntry.Callsign = q.HisCall;
                iWlDupingEntry.SerialNumber = q.SentSerialNumber;
                // todo: set time of logged QSO to last message received
                String date = String.Format("{0:yyyyMMdd}", q.TimeOfLastReceived);
                String time = String.Format("{0:HHmmss}", q.TimeOfLastReceived);
                iWlDupingEntry.SetDateTimeFromADIF(date, time);
                if (GridSquareSentFieldNumber > 0)
                    iWlDupingEntry.SetFieldN((short)GridSquareSentFieldNumber, q.SentGrid);
                if (DgtlFieldNumber > 0)
                    iWlDupingEntry.SetFieldN((short)DgtlFieldNumber, "FT8");
                switch ((ExchangeTypes)excSet)
                {
                    case ExchangeTypes.ARRL_FIELD_DAY:
                        LogFdQso(q);
                        break;
                    case ExchangeTypes.ARRL_RTTY:
                        LogRttyRoundUpQso(q);
                        break;

                    case ExchangeTypes.DB_REPORT:
                    case ExchangeTypes.GRID_SQUARE:
                    case ExchangeTypes.GRID_SQUARE_PLUS_REPORT:
                        LogGridSquareQso(q);
                        break;
                }
                iWlDupingEntry.ClearEntry();
            }
            q.MarkedAsLogged = true;
        }

        private void LogFdQso(QsoInProgress q)
        {
            // start at latest and work backwards
            for (int i = q.MessageList.Count - 1; i >= 0; i -= 1)
            {
                XDpack77.Pack77Message.ReceivedMessage rm = q.MessageList[i];
                XDpack77.Pack77Message.RttyRoundUpMessage iexc = 
                    rm.Pack77Message as XDpack77.Pack77Message.RttyRoundUpMessage;
                if (iexc == null)
                    continue;
                var fields = iexc.Exchange.Split((char[])null);
                iWlDupingEntry.SetFieldN(2, fields[0]);
                iWlDupingEntry.SetFieldN(3, fields[1]);
                break;
            }
            iWlDupingEntry.EnterQso();
        }

        private void LogRttyRoundUpQso(QsoInProgress q)
        {
            if (SentRstFieldNumber > 0)
                iWlDupingEntry.SetFieldN((short)SentRstFieldNumber, q.Message.RST);
            // RCV RST and "QTH" which might be serial number
            for (int i = q.MessageList.Count - 1; i >= 0; i -= 1)
            {   // from most recent back to oldest
                XDpack77.Pack77Message.ReceivedMessage rm = q.MessageList[i];
                XDpack77.Pack77Message.RttyRoundUpMessage iexc = 
                    rm.Pack77Message as XDpack77.Pack77Message.RttyRoundUpMessage;
                if (iexc == null)
                    continue;
                // found last RTTY Roundup message
                string exc = iexc.Exchange;
                var fields = exc.Split((char[])null);
                iWlDupingEntry.SetFieldN(3, fields[0]); // received rst
                iWlDupingEntry.SetFieldN(4, fields[1]); // received state/serial
                break;
            }
            iWlDupingEntry.EnterQso();
        }

        private void LogGridSquareQso(QsoInProgress q)
        {
            /* This function works for both grid square and
             * signal report exchange. Search the list of
             * messages in the QSO for both and add what we find to the log.
             */
            if (SentRstFieldNumber > 0)
            {   // the log has a column for RST
                int incomingDB = q.Message.SignalDB;
                string db = incomingDB.ToString("D2");
                if (incomingDB>=0)
                    db = "+" + db;
                // put the received dB indicator in the log. might overwrite below.
                iWlDupingEntry.SetFieldN((short) SentRstFieldNumber, db);
            }
            string dbReport = null;
            bool foundRstReport = false;
            foreach (var m in q.MessageList)
            {
                XDpack77.Pack77Message.Exchange iExc = m.Pack77Message as XDpack77.Pack77Message.Exchange;
                if (iExc == null)
                    continue;
                // find received message with a grid square in it
                string gridsquare = iExc.GridSquare;
                if (!String.IsNullOrEmpty(gridsquare) && (GridSquareReceivedFieldNumber > 0))
                    iWlDupingEntry.SetFieldN((short)GridSquareReceivedFieldNumber, gridsquare);
                // find received message with an RST
                string rst = iExc.RST;
                if (!String.IsNullOrEmpty(rst) && (ReceivedRstFieldNumber > 0))
                {
                    iWlDupingEntry.SetFieldN((short)ReceivedRstFieldNumber, rst);
                    foundRstReport = true;
                }
                int db = iExc.SignaldB;
                if (db > XDpack77.Pack77Message.Message.NO_DB)
                    dbReport = String.Format("{0:+00;-00;+00}", db);
            }
            // if the exchange we used had an RST, we did it above. If not, use dB
            if (!foundRstReport && !String.IsNullOrEmpty(dbReport) && (ReceivedRstFieldNumber > 0))
                iWlDupingEntry.SetFieldN((short)ReceivedRstFieldNumber, dbReport);
            iWlDupingEntry.EnterQso();
        }
        
         #endregion

        private String MyCall {
            set { 
                myCall = value;
                myBaseCall = value;
                if (!String.IsNullOrEmpty(value) && !XDft.Generator.checkCall(myCall, ref myBaseCall))
                    MessageBox.Show("Callsign " + value + " is not a valid callsign for FT8");
            }
        }

#if DEBUG // for the simulator
        const int INVALID_TIME_SECONDS = -1 - (60 * 60);
        static int timeStampSeconds(String s, out bool isOdd)
        {
            isOdd = false;
            if (String.IsNullOrEmpty(s))
                return -1;
            if (s.Length < 6)
                return -1;
            try
            {
                int seconds = Int32.Parse(s.Substring(4, 2));
                isOdd = 0 != (1 & (seconds / FT8_SEC));
                return seconds +
                    60 * Int32.Parse(s.Substring(2,2));
            }
            catch (System.Exception )
            {  return INVALID_TIME_SECONDS;   }
        }
#endif

        #region Form events
        private static bool fromRegistryValue(Microsoft.Win32.RegistryKey rk, string valueName, out int v)
        {
            object rv = rk.GetValue(valueName);
            v = 0;
            if (rv == null)
                return false;
            return Int32.TryParse(rv.ToString(), out v);
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            rxForm = new XcvrForm(this, instanceNumber);
            this.Text = String.Format("{0}-{1}", this.Text, instanceNumber);

            ClockLabel cl = new ClockLabel();
            cl.Location = labelClockAnimation.Location;
            cl.Size = labelClockAnimation.Size;
            panel3.Controls.Remove(labelClockAnimation);
            panel3.Controls.Add(cl);
            labelClockAnimation.Dispose();
            labelClockAnimation = cl;

            // Apply color scheme
            label4.BackColor =
            label5.BackColor =
            label3.BackColor =
            label11.BackColor =
            panel5.BackColor = 
            panel6.BackColor =
            panel3.BackColor =
            label7.BackColor =
            menuStrip.BackColor =
            panel1.BackColor = CustomColors.CommonBackgroundColor;
            groupBox3.BackColor =
            trackBarTxGain.BackColor = CustomColors.TxBackgroundColor;

            SetupTxAndRxDeviceIndicies();
            
            while (true)
            {
                myGrid = Properties.Settings.Default.MyGrid;
                if (SetupForm.validateGridSquare(myGrid) && InitSoundInAndOut())
                    break;
                var sf = new SetupForm(
                    instanceNumber,
                    SetupMaySelectDevices, SetupMaySelectLR,
                    null == iWlEntry);
                sf.controlSplit = controlVFOsplit;
                sf.forceRigUsb = forceRigUsb;
                sf.txHighLimit = TxHighFreqLimit;
                if (sf.ShowDialog() != DialogResult.OK)
                    {
                        Close();
                        return;
                    }
                controlVFOsplit = sf.controlSplit;
                forceRigUsb = sf.forceRigUsb;
                TxHighFreqLimit = sf.txHighLimit;
                MyCall = Properties.Settings.Default.CallUsed.ToUpper();
            }

            cqListEven = new CallPresentation(panelEvenCQs, labelCqTable, checkBoxCqTable);
            cqListEven.InitiateQsoCb += new CallPresentation.InitiateQso(InitiateQsoFromMessage);
            cqListOdd = new CallPresentation(panelOddCQs, labelCqTable, checkBoxCqTable);
            cqListOdd.InitiateQsoCb += new CallPresentation.InitiateQso(InitiateQsoFromMessage);
            toMe = new CallPresentation(listToMe, labelCqTable, checkBoxCqTable);
            toMe.InitiateQsoCb += new CallPresentation.InitiateQso(InitiateQsoFromMessage);
            qsosPanel = new QsosPanel(panelInProgress, labelInProgress, checkBoxInProgress);
            qsosPanel.fillAlternatives += new QsosPanel.FillAlternatives(fillAlternativeMessages);
            qsosPanel.qsoActiveChanged += new QsosPanel.QsoActiveChanged(OnQsoActiveChanged);
            qsosPanel.onRemovedQso += new QsosPanel.OnRemovedQso(quitQso);
            qsosPanel.logAsIs += new QsosPanel.LogAsIs(LogQso);
            qsosPanel.isCurrentCycle += new QsosPanel.IsCurrentCycle((QsoInProgress q) =>
                { return ((q.Message.CycleNumber & 1) != 0) == radioButtonEven.Checked; } );
            qsosPanel.orderChanged += new QsosPanel.OrderChanged(() => checkedlbNextToSend.Sort());
            cqListEven.Reset();
            cqListOdd.Reset();
            toMe.Reset();
            cqListEven.SizeChanged(null, null);
            cqListOdd.SizeChanged(null, null);
            toMe.SizeChanged(null, null);
            qsosPanel.SizeChanged(null, null);

            initQsoQueue();
            
            rxForm.logFile = logFile;
            rxForm.Show();
#if DEBUG
            try
            {
                using (var simContents = System.IO.File.OpenText(@"C:\temp\decoded.txt"))
                {
                    string s = "";
                    while ((s = simContents.ReadLine()) != null)
                    {
                        if (s.Length > 9)
                        {
                            s = s.Substring(9);
                            var split =s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                            int dummy;
                            if ((split.Length > 5) && Int32.TryParse(split[0], out dummy))
                            {
                                if (simulatorLines == null)
                                    simulatorLines = new List<string>();
                                simulatorLines.Add(s);
                            }
                        }
                    }

                        bool isOdd;
                    if (simulatorLines != null && simulatorLines.Count > 0)
                        simulatorTimeOrigin = timeStampSeconds(simulatorLines[0], out isOdd);
                }
            }
            catch (System.Exception)
            { /* do nothing */}
            simulatorStart = DateTime.UtcNow;
#endif

            Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(instanceRegKeyName);
            int decodeMin = 100;
            int decodeMax = 5000;
            if (null != rk)
            {   // used saved settings
                {
                    int x;
                    if (fromRegistryValue(rk, "ControlVfoSplit", out x) && x > 0)
                    {
                        int m;
                        if (fromRegistryValue(rk, "MaxTxAudioFrequency", out m) && m > 0)
                        {
                            controlVFOsplit = true;
                            TxHighFreqLimit = m;
                        }
                    }
                }
                {
                    int x;
                    if (fromRegistryValue(rk, "ForceRigUSB", out x) && x > 0)
                        forceRigUsb = true;
                }
                {
                    int x, y;
                    if (fromRegistryValue(rk, "MainX", out x) && fromRegistryValue(rk, "MainY", out y))
                    {
                        System.Drawing.Point cornerMe = new System.Drawing.Point(x, y);
                        foreach (Screen s in Screen.AllScreens)
                        {
                            if (s.WorkingArea.Contains(cornerMe))
                            {
                                StartPosition = FormStartPosition.Manual;
                                Location = cornerMe;
                                break;
                            }
                        }
                    }
                }
                {
                    int w, h;
                    if (fromRegistryValue(rk, "MainW", out w) && fromRegistryValue(rk, "MainH", out h))
                    {
                        if (w < 280)
                            w = 280;
                        if (h < 280)
                            h = 280;
                        Size = new System.Drawing.Size(w, h);
                    }
                }

                int mainSplitterDistance;
                if (fromRegistryValue(rk, "MainSplit", out mainSplitterDistance))
                {  
                    if (mainSplitterDistance >= splitContainerCqLeft.Panel1MinSize && mainSplitterDistance <= 
                        splitContainerCQ.Width - splitContainerCQ.Panel2MinSize)
                        splitContainerCqLeft.SplitterDistance = mainSplitterDistance; 
                }    

                {
                    int x, y;
                    if (fromRegistryValue(rk, "XcvrX", out x) && fromRegistryValue(rk, "XcvrY", out y))
                    {
                        System.Drawing.Point cornerXcvr = new System.Drawing.Point(x, y);
                        foreach (Screen s in Screen.AllScreens)
                        {
                            if (s.WorkingArea.Contains(cornerXcvr))
                            {
                                rxForm.Location = cornerXcvr;
                                break;
                            }
                        }
                    }
                }
                {
                    int w, h;
                    if (fromRegistryValue(rk, "XcvrW",  out w) && fromRegistryValue(rk, "XcvrH", out h))
                    {
                        if (w < 280)
                            w = 280;
                        if (h < 280)
                            h = 280;
                        rxForm.Size = new System.Drawing.Size(w, h);
                    }
                }

                int xcvrSplitterDistance;
                if (fromRegistryValue(rk, "XcvrSplit", out xcvrSplitterDistance))
                    try
                    {  rxForm.SplitterDistance = xcvrSplitterDistance;   }
                    finally { }
                int txEven;
                if (fromRegistryValue(rk, "TxEven", out txEven))
                {
                    if (txEven == 0)
                        radioButtonOdd.Checked = true;
                    else
                        radioButtonEven.Checked = true;
                }
                int cqBoth;
                if (fromRegistryValue(rk, "BothCQsShow", out cqBoth))
                    checkBoxCQboth.Checked = cqBoth != 0;
                int txFreq;
                if (fromRegistryValue(rk, "TXfrequency", out txFreq))
                    TxFrequency = txFreq;
                int temp;
                if (fromRegistryValue(rk, "DecodeMinHz", out temp))
                {
                    decodeMin = temp;
                    if (decodeMin < 100)
                        decodeMin = 100;
                    if (decodeMin > 4500)
                        decodeMin = 4500;
                }
                if (fromRegistryValue(rk, "DecodeMaxHz", out temp))
                {
                    decodeMax = temp;
                    if (decodeMax < 100)
                        decodeMax = 100;
                    if (decodeMax > 5000)
                        decodeMax = 5000;
                    if (decodeMax <= decodeMin)
                        decodeMax = decodeMin + 60;
                }
            }

            deviceTx.TransmitCycle = radioButtonEven.Checked ?
                XD.Transmit_Cycle.PLAY_EVEN_15S : XD.Transmit_Cycle.PLAY_ODD_15S;
            locationToSave = Location;
            sizeToSave = Size;
            checkBoxShowMenu.Checked = Properties.Settings.Default.ShowMenu;
            rxForm.RxHz = (int)numericUpDownRxFrequency.Value;
            rxForm.MinDecodeFrequency = decodeMin;
            rxForm.MaxDecodeFrequency = decodeMax;
            listBoxConversation.DrawMode = DrawMode.OwnerDrawFixed;
            logFile.SendToLog("Started");
        }

        private void initQsoQueue()
        {
            // the two-message exchanges per QSO sequencing is different
            if (ExchangeTypes.GRID_SQUARE_PLUS_REPORT != 
                    (ExchangeTypes)Properties.Settings.Default.ContestExchange)
                qsoQueue = new QsoQueue(qsosPanel, this);
            else // has its own class
                qsoQueue = new Qso2MessageExchange(qsosPanel, this);
            qsoQueue.MyCall = myCall;
            qsoQueue.MyBaseCall = myBaseCall;
            qsosPanel.Reset(); // no QSOs in progress can survive switching queue handling
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            Properties.Settings.Default.ShowMenu = checkBoxShowMenu.Checked;
            Properties.Settings.Default.Save();
            Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(instanceRegKeyName);
            if (null != rk)
            {   // save windows positions, etc.
                rk.SetValue("MainX", locationToSave.X.ToString());
                rk.SetValue("MainY", locationToSave.Y.ToString());
                rk.SetValue("MainW", sizeToSave.Width.ToString());
                rk.SetValue("MainH", sizeToSave.Height.ToString());
                rk.SetValue("MainSplit", splitContainerCqLeft.SplitterDistance.ToString());
                rk.SetValue("XcvrX", rxForm.LocationToSave.X.ToString());
                rk.SetValue("XcvrY", rxForm.LocationToSave.Y.ToString());
                rk.SetValue("XcvrW", rxForm.SizeToSave.Width.ToString());
                rk.SetValue("XcvrH", rxForm.SizeToSave.Height.ToString());
                rk.SetValue("XcvrSplit", rxForm.SplitterDistance.ToString());
                if (!controlVFOsplit)
                    rk.SetValue("ControlVfoSplit", "0");
                else
                {
                    rk.SetValue("ControlVfoSplit", "1");
                    rk.SetValue("MaxTxAudioFrequency", TxHighFreqLimit.ToString());
                }
                rk.SetValue("ForceRigUSB", forceRigUsb ? "1" : "0");
                rk.SetValue("TxEven", radioButtonEven.Checked ? "1" : "0");
                rk.SetValue("BothCQsShow", checkBoxCQboth.Checked ? "1" : "0");
                rk.SetValue("TXfrequency", numericUpDownFrequency.Value.ToString());
                rk.SetValue("DecodeMinHz", rxForm.MinDecodeFrequency.ToString());
                rk.SetValue("DecodeMaxHz", rxForm.MaxDecodeFrequency.ToString());
            }
            if (demodulator != null)
                demodulator.Dispose();
            demodulator = null;

            if (wsjtExe != null)
                wsjtExe.Dispose();
            wsjtExe = null;

            if (wsjtSharedMem != null)
                wsjtSharedMem.Dispose();
            wsjtSharedMem = null;

            if (waveDevicePlayer != null)
                waveDevicePlayer.Dispose();
            waveDevicePlayer = null;

            if (logFile != null)
            {
                logFile.SendToLog("Closed");
                logFile.Dispose();
            }
            logFile = null;

            if (conversationLogFile != null)
                conversationLogFile.Dispose();
            conversationLogFile = null;
        }

        private void SetupTxAndRxDeviceIndicies()
        {
            // default device select to what's in settings
            TxOutDevice = StringToIndex(Properties.Settings.Default["AudioOutputDevice_" + instanceNumber.ToString()].ToString(),
                XD.WaveDeviceEnumerator.waveOutDevices());
            RxInDevice = StringToIndex(Properties.Settings.Default["AudioInputDevice_" + instanceNumber.ToString()].ToString(),
               XD.WaveDeviceEnumerator.waveInDevices());
            if (iWlEntry != null)
            {   // we are connected to WriteLog's automation interface
                var lr = iWlEntry.GetLeftRight();
                if ((lr != 0) && (lr != 1))
                {
                    // instance #1 is allowed to select neither L nor R
                    if ((instanceNumber == 1) && (lr == (short)-1))
                        lr = 0; // put on left
                    else if (lr > 1)
                    { // Radio #3 or #4
                        return; // use setup form
                    }
                    else
                    {
                        MessageBox.Show("FtRite requires the Entry Window to be set to either L or R");
                        return;
                    }
                }
                SetupMaySelectDevices = false;
                var RxInDevices = XD.WaveDeviceEnumerator.waveInDevices();
                var TxOutDevices = XD.WaveDeviceEnumerator.waveOutDevices();
                Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    "Software\\W5XD\\writelog.ini\\OneDevicePerRadio");
                if ((rk != null) && (rk.ValueCount > 1))
                {
                    // WL Sound Mixer is set up one-device-per-radio
                    object v = null;
                    RxInDevice = UInt32.MaxValue;
                    TxOutDevice = UInt32.MaxValue;
                    if (rk != null)
                    {
                        // RX side
                        v = rk.GetValue(lr == 0 ? "LeftReceiverAudioDeviceId" : "RightReceiverAudioDeviceId");
                        if (v != null)
                        {
                            string id = v.ToString().ToUpper();
                            for (int i = 0; i < RxInDevices.Count; i++)
                            {
                                if (String.Equals(XD.WaveDeviceEnumerator.waveInInstanceId(i).ToUpper(), id))
                                {
#if DEBUG
                                    // have a look at the user-friendly name of the device
                                    var waveIns = XD.WaveDeviceEnumerator.waveInDevices();
                                    if (i < waveIns.Count)
                                    {
                                        string name = waveIns[i];
                                    }
#endif
                                    RxInDevice = (uint)i;
                                    break;
                                }
                            }
                            if (RxInDevice < 0)
                                MessageBox.Show("Use WriteLog Sound Mixer to set the " + (lr == 0 ? "Left" : "Right") + " Rx audio in");
                        }
                        else
                            MessageBox.Show("WriteLog Sound Mixer control is not set up for " + (lr == 0 ? "Left" : "Right") + " RX audio in");
                        // TX side
                        v = rk.GetValue(lr == 0 ? "LeftTransmitterAudioDeviceId" : "RightTransmitterAudioDeviceId");
                        if (v != null)
                        {
                            string id = v.ToString().ToUpper();
                            for (int i = TxOutDevices.Count-1; i >= 0 ; i -= 1)
                            {
                                if (XD.WaveDeviceEnumerator.waveOutInstanceId(i).ToUpper() == id)
                                {
                                    TxOutDevice = (uint)i;
                                    break;
                                }
                            }
                            if (TxOutDevice < 0)
                                MessageBox.Show("Use WriteLog Sound Mixer to set the " + (lr == 0 ? "Left" : "Right") + " TX audio out");
                        }
                        else
                            MessageBox.Show("WriteLog Sound Mixer control is not set up for " + (lr == 0 ? "Left" : "Right") + " RX audio in");
                    }
                }
                else
                {
                    // WL Sound Mixer is set up for separate device per radio
                    object v = null;
                    rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\W5XD\\writelog.ini\\WlSound");
                    if (rk != null)
                        v = rk.GetValue("RxInDevice");
                    if (v != null)
                    {
                        string RxInDeviceName = v.ToString().ToUpper();
                        for (int i = RxInDevices.Count-1; i >= 0 ; i -= 1)
                        {
                            if (RxInDevices[i].ToUpper().Contains(RxInDeviceName))
                            {
                                RxInDevice = (uint)i;
                                break;
                            }
                        }
                    }
                    else
                        MessageBox.Show("WriteLog Sound Mixer control is not set up for RX audio in");
                    v = rk.GetValue("TxOutDevice");
                    if (v != null)
                    {
                        string TxOutDeviceName = v.ToString().ToUpper();
                        for (int i = 0; i < TxOutDevices.Count; i++)
                        {
                            if (TxOutDevices[i].ToUpper().Contains(TxOutDeviceName))
                            {
                                TxOutDevice = (uint)i;
                                break;
                            }
                        }
                    }
                    else
                        MessageBox.Show("WriteLog Sound Mixer control is not set up for TX audio out");
                    Properties.Settings.Default["AudioInputChannel_" + instanceNumber.ToString()] = (uint)lr;
                    Properties.Settings.Default["AudioOutputChannel_" + instanceNumber.ToString()] = (uint)lr;
                    SetupMaySelectLR = false;
                }
                MyCall = iWlDoc.CallUsed.ToUpper();
                if (!String.IsNullOrEmpty(myCall))
                    Properties.Settings.Default.CallUsed = myCall;
            }
            else
                MyCall = Properties.Settings.Default.CallUsed.ToUpper();
        }

        private void timerSpectrum_Tick(object sender, EventArgs e)
        {
            if ((null != rxForm) && (null != demodulator))
                rxForm.DisplaySpectrum(demodulator);
        }

        private System.Drawing.Point locationToSave;
        private System.Drawing.Size sizeToSave;
        private void MainForm_LocationChanged(object sender, EventArgs e)
        {
            if (WindowState != FormWindowState.Minimized)
                locationToSave = Location;
        }

        private void MainForm_SizeChanged(object sender, EventArgs e)
        {
            if (WindowState != FormWindowState.Minimized)
                sizeToSave = Size;
        }

        private delegate void OneAtATimeDel();
        private bool inOneAtATime = false;
        private List<OneAtATimeDel> oneAtATimeList = new List<OneAtATimeDel>();
        void OneAtATime(OneAtATimeDel d)
        {
            /* Calls out on COM automation are opportunities to
             * reenter this class. That reentrancy is is handled here 
             * (i.e. prevented) by keeping a queue in oneAtATimeList */
            if (inOneAtATime)
            {
                oneAtATimeList.Add(d);
                return;
            }
            inOneAtATime = true;
            d();
            while (oneAtATimeList.Any())
            {
                var deferred = oneAtATimeList.First();
                oneAtATimeList.RemoveAt(0);
                deferred();
            }
            inOneAtATime = false;
        }

        private bool inClockTick = false;
        private bool zeroIntervalCalled = false;
        private bool cqClearCalled = false;
        private bool transmitAtZeroCalled = false;

        private uint interval = 0;
        private int cycleNumber = 0;
        const uint CLEAR_OLD_MESSAGES_AT = 5;
        const uint START_LATE_MESSAGES_THROUGH = 6;

        /* having a clock to call the decoder simplifies
        ** keeping the demodulator on this gui thread.
        ** The timing of the clock is not important with
        ** the exceptiont that the decoder needs to be called close
        ** to the beginning of a cycle second...so call here 
        ** a "few" times per second. */
        private void timerFt8Clock_Tick(object sender, EventArgs e)
        {
            if (inClockTick) // don't recurse
                return; // didn't need to be here anyway
            inClockTick = true;
            var nowutc = DateTime.UtcNow;
            if ((nowutc - watchDogTime).TotalMinutes > MAX_UNANSWERED_MINUTES)
                checkBoxAutoXmit.Checked = false;
            OneAtATime(new OneAtATimeDel(() =>
            {
                try
                {
                    labelClock.Text = "";
                    const uint TRIGGER_DECODE = 5; // At this second and later, see if we see any messages
                    if ((null != demodulator) && (null != waveDevicePlayer))
                    {
                        // TRIGGER_DECODE tells the demodulator whether to actually demodulate
                        bool invokedDecode = false;
                        String hiscall = qsosPanel.FirstActive?.HisCall;
                        demodulator.mycall = myCall;
                        demodulator.hiscall = hiscall;
                        interval = demodulator.Clock(TRIGGER_DECODE, wsjtExe, ref invokedDecode, ref cycleNumber);
                        // invokedDecode tells us whether it actually was able to invoke the wsjtx decoder.
                        // Some reasons it might not: interval is less than TRIGGER_DECODE.
                        // We have recently called into Clock which did invoke a decode, and that one isn't finished yet.
                        labelClock.Text = interval.ToString();
                        // twice per second, and synced to the utc second
                        int nowmsec = DateTime.UtcNow.Millisecond;
                        if (nowmsec > 500)
                            nowmsec -= 500;
                        // stay close to the begin/middle of the UTC second
                        timerFt8Clock.Interval = 501 - nowmsec; 
                    }
                    bool isOddCycle = (cycleNumber & 1) != 0;
                    bool isTransmitCycle = radioButtonOdd.Checked == isOddCycle;
                    rxForm.OnClock(interval, isTransmitCycle);

#if DEBUG
                    const uint SIMULATOR_POPS_OUT_DECODED_MESSAGES_AT = 13;
                    if (interval == SIMULATOR_POPS_OUT_DECODED_MESSAGES_AT)
                    {   // invoke simulator in second #13
                        while (simulatorLines != null && simulatorLines.Count > simulatorNext)
                        {
                            var now = DateTime.UtcNow;
                            bool isOdd;
                            int simNextSeconds = timeStampSeconds(simulatorLines[simulatorNext], out isOdd);
                            if (simNextSeconds <= INVALID_TIME_SECONDS)
                                simulatorNext += 1; // skip it
                            else
                            {
                                int simTimeSeconds = simNextSeconds - simulatorTimeOrigin;
                                if (isOdd != isOddCycle)
                                    simTimeSeconds += 15; // delay simulation to match odd/even w.r.t. real time
                                if (simTimeSeconds < 0)
                                    simTimeSeconds += 60 * 60;
                                if (simTimeSeconds > 60 * 60)
                                    simTimeSeconds -= 60 * 60;
                                int secondsSinceOrigin = (int)(now - simulatorStart).TotalSeconds;
                                if (simTimeSeconds <= secondsSinceOrigin)
                                    OnReceived(simulatorLines[simulatorNext++], (simNextSeconds / 15) % 4);
                                else
                                    break;
                            }
                        }
                    }
#endif

                    if (interval == 0)
                    {   // FT8 cycle second zero is a special time
                        if (!zeroIntervalCalled)
                        {   // only once per cycle
                            zeroIntervalCalled = true;
                            qsoQueue.OnCycleBeginning(cycleNumber);
                            if ((null != iWlEntry) && (null != iWlDupingEntry))
                            {
                                short mode = 0;  double tx = 0;   double rx = 0; short split = 0;
                                iWlEntry.GetLogFrequency(ref mode, ref rx, ref tx, ref split);
                                mode = 6;
                                iWlDupingEntry.SetLogFrequencyEx(mode, rx, tx, split);
                                currentBand = iWlEntry.GetBand();
                            }
                            if (!isTransmitCycle)
                            {   // this is our "listen" interval
                                var q = qsosPanel.FirstActive;
                                if (null != q)
                                    fillAlternativeMessages(q);
                            }
                        }
                        if (!transmitAtZeroCalled)
                        {
                            transmitAtZero();
                            transmitAtZeroCalled = true; 
                        }
                    }
                    else
                    {
                        zeroIntervalCalled = false;
                        transmitAtZeroCalled = false;
                    }

                    if (interval == CLEAR_OLD_MESSAGES_AT)
                    {
                        if (!cqClearCalled)
                            if (isOddCycle)
                                cqListOdd.Reset();
                            else
                                cqListEven.Reset();
                        if (radioButtonEven.Checked == isOddCycle)
                            toMe.Reset();
                        cqClearCalled = true;
                    }
                    else
                        cqClearCalled = false;

                    ClockLabel cl = labelClockAnimation as ClockLabel;
                    if (null != cl)
                    {
                        cl.Seconds = interval;
                        cl.AmTransmit = isTransmitCycle;
                    }
                    if (forceRigUsb && (null != iWlEntry) && (null != iWlDupingEntry))
                    {
                        short mode = 0; double tx = 0; double rx = 0; short split = 0;
                        iWlEntry.GetLogFrequency(ref mode, ref rx, ref tx, ref split);
                        if (mode != 2)
                        {
                            mode = 2;
                            iWlEntry.SetLogFrequencyEx(mode, rx, tx, split);
                        }
                    }
                }
                finally
                { inClockTick = false; }
            }));
        }

        #endregion

        #region foreign threads
        // The XDft8 assembly invokes our delegate on a foreign thread.
        private void Decoded(String s, int cycle)
        {   // BeginInvoke back onto form's thread
            BeginInvoke(new Action<String,int>((String x, int c) => OnReceived(x,c)), s, cycle);
        }

        private void AudioBeginEnd(bool isBeginning)
        {   // get back on the form's thread
            BeginInvoke(new Action<bool>(OnAudioComplete), isBeginning);
        }
        #endregion

        private void OnAudioComplete(bool isBegin)
        {
            // tell writelog to turn on/off the PTT
            if (null != iWlEntry)
            {
                iWlEntry.SetXmitPtt((short)(isBegin ? 1 : 0));
                if (null != pttPort)
                    pttPort.RtsEnable = isBegin;
            }
            sendInProgress = isBegin;
        }

        public void SendRttyMessage(String toSend) // WriteLog pressed an F-key
        {   // automation call from WriteLog....
        }

        public void AbortMessage()
        {
            if (null != deviceTx)
                deviceTx.Abort();
            sendInProgress = false;
        }
     
        private void quitQso(QsoInProgress q)
        {
            qsosPanel.Remove(q);
            for (int i = 0; i < checkedlbNextToSend.Items.Count;)
            {
                QueuedToSendListItem qli = checkedlbNextToSend.Items[i] as QueuedToSendListItem;
                if ((null != qli) && Object.ReferenceEquals(qli.q, q))
                        checkedlbNextToSend.Items.RemoveAt(i);
                else
                    i += 1;
            }
        }

        private void buttonAbort_Click(object sender, EventArgs e)
        {
            AbortMessage();
            for (int i = 0; i < checkedlbNextToSend.Items.Count; i++)
                checkedlbNextToSend.SetItemChecked(i, false);
            for (int i = 0; i < listBoxAlternatives.Items.Count; i++)
                listBoxAlternatives.SetItemChecked(i, false);
            checkBoxAutoXmit.Checked = false;
            checkBoxManualEntry.Checked = false;
        }

        private void radioOddEven_CheckedChanged(object sender, EventArgs e)
        {
            deviceTx.TransmitCycle = radioButtonEven.Checked ? 
                XD.Transmit_Cycle.PLAY_EVEN_15S : XD.Transmit_Cycle.PLAY_ODD_15S;
            if (!checkBoxCQboth.Checked)
            {
                splitContainerCQ.Panel1Collapsed = radioButtonEven.Checked ^ false;
                splitContainerCQ.Panel2Collapsed = radioButtonEven.Checked ^ true;
            }
            qsosPanel.RefreshOnScreen();
        }

        private void checkedlbNextToSend_ItemCheck(object sender, ItemCheckEventArgs e)
        {// ItemCheck event preceeds checking the check box. 
            // ..but I want the check box true before calling transmitAtZero...
            // ..so have to work around recursion issues
                bool checkState = e.NewValue == CheckState.Checked;
                if (checkState)
                    BeginInvoke(new Action(() => {
                        if (!sendInProgress && interval <= START_LATE_MESSAGES_THROUGH) 
                            transmitAtZero(true);
                        }));
        }

        private void checkBoxAutoXmit_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBoxAutoXmit.Checked)
                    watchDogTime = DateTime.UtcNow;
            for (int i = 0; i < checkedlbNextToSend.Items.Count; i++)
                checkedlbNextToSend.SetItemChecked(i, checkBoxAutoXmit.Checked);
        }

        private void listBoxAlternatives_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (e.NewValue == CheckState.Checked)
            {   // turn all the others off
                for (int i = 0; i < listBoxAlternatives.Items.Count; i++)
                {
                    if (i == e.Index)
                        continue;
                    listBoxAlternatives.SetItemChecked(i, false);
                }
            }
        }

        private void textBoxMessageEdit_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar == '\r')
            {   // user typed CR with focus in manual entry text box
                e.Handled = true;
                checkBoxManualEntry.Checked = true;
            }
        }

        private void listBoxAlternatives_SelectedIndexChanged(object sender, EventArgs e)
        {
            QueuedToSendListItem qli = listBoxAlternatives.SelectedItem as QueuedToSendListItem;
            if (null != qli)
                textBoxMessageEdit.Text = qli.MessageText;
        }

        private void checkBoxCQ_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBoxCQ.Checked)
                watchDogTime = DateTime.UtcNow;
        }

        private void checkBoxRespondAny_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBoxRespondAny.Checked)
            {
                watchDogTime = DateTime.UtcNow;
                checkBoxAutoXmit.Checked = true;
            }
        }

        private void OnQsoActiveChanged(QsoInProgress q)
        {
            for (int i = 0; i < checkedlbNextToSend.Items.Count; i++)
            {
                QueuedToSendListItem qli = checkedlbNextToSend.Items[i] as QueuedToSendListItem;
                if ((null != qli) && Object.ReferenceEquals(qli.q, q))
                    checkedlbNextToSend.SetItemChecked(i, q.Active && checkBoxAutoXmit.Checked);
            }
        }

        private void checkBoxShowMenu_CheckedChanged(object sender, EventArgs e)
        { menuStrip.Visible = checkBoxShowMenu.Checked;  }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {  Close(); }

        private void setupToolStripMenuItem_Click(object sender, EventArgs e)
        {
            bool maySelectCallUsed = null == iWlEntry;
            if (!maySelectCallUsed)
            {
                String curCall = iWlDoc.CallUsed;
                if (!String.IsNullOrEmpty(curCall))
                {
                    Properties.Settings.Default.CallUsed = curCall.ToUpper();
                    MyCall = curCall;
                    qsoQueue.MyCall = myCall;
                    qsoQueue.MyBaseCall = myBaseCall;
                }
            }
            var form = new SetupForm(instanceNumber,
                SetupMaySelectDevices, SetupMaySelectLR,
                maySelectCallUsed);
            form.controlSplit = controlVFOsplit;
            form.forceRigUsb = forceRigUsb;
            form.txHighLimit = TxHighFreqLimit;
            var res = form.ShowDialog();
            if (res == DialogResult.OK)
            {
                controlVFOsplit = form.controlSplit;
                forceRigUsb = form.forceRigUsb;
                TxHighFreqLimit = form.txHighLimit;
                if (SetupMaySelectDevices)
                {
                    if (form.whichRxDevice >= 0)
                        RxInDevice = (uint)form.whichRxDevice;
                    if (form.whichTxDevice >= 0)
                        TxOutDevice = (uint)form.whichTxDevice;
                }
                if (maySelectCallUsed)
                {
                    MyCall = Properties.Settings.Default.CallUsed.ToUpper();
                    qsoQueue.MyCall = myCall;
                    qsoQueue.MyBaseCall = myBaseCall;
                }
                InitSoundInAndOut();
                initQsoQueue();
            }
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {  new AboutForm().ShowDialog(); }

        private void checkBoxCQboth_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBoxCQboth.Checked)
            {
                splitContainerCQ.Panel1Collapsed = false;
                splitContainerCQ.Panel2Collapsed = false;
            }
            else
            {
                splitContainerCQ.Panel1Collapsed = radioButtonEven.Checked ^ false;
                splitContainerCQ.Panel2Collapsed = radioButtonEven.Checked ^ true;
            }
        }

        private void viewLogToolStripMenuItem_Click(object sender, EventArgs e)
        {
            logFile.Flush();
            System.Diagnostics.Process.Start(LogFilePath);
        }

        private void viewReadMeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string readme = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location)
                + "\\ReadMe.htm";
            try
            { System.Diagnostics.Process.Start(readme); }
            catch (System.Exception) { }
        }
        
        private void helpToolStripMenuItem_DropDownOpened(object sender, EventArgs e)
        {
            ToolStripMenuItem tsmi = sender as ToolStripMenuItem;
            if (null != tsmi)
            {
                var logLength = LogFile.LogFileLength(LogFilePath);
                if (logLength != 0)
                    logFileLengthToolStripMenuItem.Text = 
                        String.Format("Log file length: {0:#,##0.0} MB", logLength/(1024.0*1024.0));
            }
        }

        private void resetLogFileToEmpyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(this, "Are you sure you want to clear your log file?", 
                "DigiRite", MessageBoxButtons.YesNo) == DialogResult.Yes)
                logFile.ResetToEmpty(LogFilePath);
        }

        private void removeAllInactiveToolStripMenuItem_Click(object sender, EventArgs e)
        { qsosPanel.removeAllInactive();  }

        private void removeAllLoggedToolStripMenuItem_Click(object sender, EventArgs e)
        {  qsosPanel.removeAllLogged();       }

        private void trackBarTxGain_Scroll(object sender, EventArgs e)
        {   deviceTx.Gain = (float)Math.Pow(2.0, (trackBarTxGain.Value - trackBarTxGain.Maximum)/AUDIO_SLIDER_SCALE);  }
        
        private void buttonTune_Click(object sender, EventArgs e)
        {
            if (sendInProgress)
                return;
            deviceTx.TransmitCycle = XD.Transmit_Cycle.PLAY_NOW;
            const int tuneFrequency = 1000;
            const int TUNE_LEN = 19;
            RigVfoSplitForTx(tuneFrequency, tuneFrequency + 60);
            int[] it = new int[TUNE_LEN];
            XDft.Generator.Play(it, tuneFrequency, deviceTx.GetRealTimeAudioSink());
        }

        #region TX RX frequency

        public int TxFrequency {
            get {
                return (int)numericUpDownFrequency.Value;
            }
            set {
                if ((value <= 0) || (value > 6000))
                    return;
                numericUpDownFrequency.Value = value;
            }
        }

        public int RxFrequency {
            get {
                if (null != demodulator)
                    return (int)demodulator.nfqso;
                return 0;
            }
            set {
                if ((value <= 0) || (value > 6000))
                    return;
                int v = value;
                if (null != demodulator)
                    demodulator.nfqso = v;
                rxForm.RxHz = v;
                if (v != (int)numericUpDownRxFrequency.Value)
                    numericUpDownRxFrequency.Value = v;
            }
        }
        
        private void numericUpDownFrequency_ValueChanged(object sender, EventArgs e)
        { rxForm.TxHz = TxFrequency; }

        private void numericUpDownRxFrequency_ValueChanged(object sender, EventArgs e)
        { RxFrequency = (int)numericUpDownRxFrequency.Value;}

        private void buttonEqTx_Click(object sender, EventArgs e)
        { numericUpDownRxFrequency.Value = numericUpDownFrequency.Value; }

        private void buttonEqRx_Click(object sender, EventArgs e)
        { numericUpDownFrequency.Value = numericUpDownRxFrequency.Value; }

        #endregion

     }
}