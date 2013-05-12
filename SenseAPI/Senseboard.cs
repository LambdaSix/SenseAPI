using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace SenseAPI
{
    internal enum SenseState
    {
        FirstAckHeaderReceived,
        SecondAckHeaderReceived,
        Idle,
        BurstHeaderRecieved,
        FirstBurstByteReceived
    }

    public enum LightState : int
    {
        On,
        Off
    }

    [Flags]
    public enum LightMask : byte
    {
        None = 0,
        One = 0x1,
        Two = 0x2,
        Three = 0x4,
        Four = 0x8,
        Five = 0x10,
        Six = 0x20,
        Seven = 0x40,
        All = 0xFF,
    }

    public enum Servo
    {
        A = 0,
        B = 1
    }

    [Flags]
    public enum SensorFilter : byte
    {
        None = 0,
        Slider = 0x1,
        Infrared = 0x2,
        Sound = 0x4,
        Button = 0x8,
        InputA = 0x10,
        InputB = 0x20,
        InputC = 0x40,
        InputD = 0xFF
    }

    internal class SenseSerialConstants
    {
        private static byte _firstAckHeader = 0x55;
        private static byte _secondAckHeader = 0xFF;
        private static byte _burstHeader = 0x0C;
        private static byte _ackByte = 0xAA;
        private static int _timeout = 1000;
        private static int _maxRetries = 1;

        public static byte FirstAckHeader {
            get { return _firstAckHeader; }
            set { _firstAckHeader = value; }
        }

        public static byte SecondAckHeader {
            get { return _secondAckHeader; }
            set { _secondAckHeader = value; }
        }

        public static byte BurstHeader {
            get { return _burstHeader; }
            set { _burstHeader = value; }
        }

        public static byte AckByte {
            get { return _ackByte; }
            set { _ackByte = value; }
        }

        public static int Timeout {
            get { return _timeout; }
            set { _timeout = value; }
        }

        public static int MaxRetries {
            get { return _maxRetries; }
            set { _maxRetries = value; }
        }
    }

    public class ResistiveInputs
    {
        private int[] _store = new int[4];

        public ResistiveInputs(Action<object, Resistor> eventInvoke) {
            eventInvoker = eventInvoke;
        }

        private Action<object, Resistor> eventInvoker;

        public int this[int i] {
            get { return _store[i]; }
            set {
                if (_store[i] != value) {
                    _store[i] = value;

                    eventInvoker(this, new Resistor {Input = i, Value = value});
                }
            }
        }
    }

    public class Resistor
    {
        public int Input { get; set; }
        public int Value { get; set; }
    }

    public class Senseboard
    {
        private readonly string _comPort;
        private readonly ResistiveInputs _resistiveInputs;
        private bool _ackReceived;
        private bool _button;
        private bool _continue;
        private bool _infrared;
        private bool _initialized;
        private Thread _pollingThread;
        private SerialPort _serialPort;
        private int _slider;
        private int _sound;
        private SenseState _state;

        /* Events */
        public event EventHandler<int> OnSliderChanged = delegate { }; 
        public event EventHandler<int> OnSoundChanged = delegate { };
        public event EventHandler<bool> OnInfraredChanged = delegate { };
        public event EventHandler<bool> OnButtonChanged = delegate { };
        public event EventHandler<Resistor> OnResistiveInputChanged = delegate { }; 

        public int Slider {
            get { return _slider; }
            set {
                if (_slider != value) {
                    _slider = value;
                    // Notify listeners
                    OnSliderChanged(this, value);
                }
            }
        }

        public ResistiveInputs ResistiveInputs {
            get { return _resistiveInputs; }
        }

        public int Sound {
            get { return _sound; }
            set {
                if (_sound != value) {
                    _sound = value;
                    // Notify listeners.
                    OnSoundChanged(this, value);
                }
            }
        }

        public bool Infrared {
            get { return _infrared; }
            set {
                if (_infrared != value) {
                    _infrared = value;
                    // Notify listeners.
                    OnInfraredChanged(this, value);
                }
            }
        }

        public bool Button {
            get { return _button; }
            set {
                if (_button != value) {
                    _button = value;
                    // Notify listeners.
                    OnButtonChanged(this, value);
                }
            }
        }

        public Senseboard(string comPort) {
            _comPort = comPort;
            _resistiveInputs = new ResistiveInputs((o, resistor) => {
                Debug.WriteLine("Resistor lambda invoked");
                OnResistiveInputChanged(o, resistor);
            });
            Initialize(_comPort);
        }

        private void Shutdown() {
            _pollingThread.Join();
            _serialPort.Close();
        }

        public void Initialize(string comPort) {
            _serialPort = new SerialPort(comPort, 115200, Parity.None, 8, StopBits.One);
            _serialPort.Handshake = Handshake.None;
            _serialPort.ReadTimeout = 500;
            _serialPort.WriteTimeout = 500;

            _serialPort.Open();

            _pollingThread = new Thread(PollData);
            _pollingThread.Start();
            _continue = true;

            /* Setup the burst mode. */
            Action setBurst = async () => {
                bool result = await SetBurstMode();
                Debug.WriteLine(result ? "Burst mode set" : "Burst mode fail");
                _initialized = true;
            };

            setBurst();
        }

        private bool CheckReady() {
            if (!_initialized)
                throw new Exception("Not initialized");
            return true;
        }

        #region LED control

        /// <summary>
        /// Sets an individual LED on or off.
        /// </summary>
        /// <param name="ledIdx">The LED to alter, 1-7</param>
        /// <param name="state">Turn the LED on (true) or off (false)</param>
        public void SetLED(int ledIdx, LightState state) {
            CheckReady();

            byte onByte = (state == LightState.On) ? (byte) 0xC1 : (byte) 0xC0;

            Task<bool> result = SendData(new byte[] {0x54, 0xFE, onByte, (byte) (1 << ledIdx - 1)});
        }

        public void SetLED(LightMask mask, LightState state) {
            CheckReady();

            byte onByte = (state == LightState.On) ? (byte) 0xC1 : (byte) 0xC0;
            var command = new byte[] {
                0x54, 0xFE, onByte, (byte) mask
            };

            Action sendData = async () => await SendData(command);
            sendData();
        }

        #endregion

        #region Stepper Motor Control

        /// <summary>
        /// Turn the attached stepper motor by the given number of steps.
        /// 
        /// Positive arguments turn clockwise, negative turns counter-clockwise.
        /// <remarks>
        /// Does nothing if the 9V Battery is not attached to the senseboard.
        /// The protocol still pretends like everything was fine however.
        /// </remarks>
        /// </summary>
        /// <param name="steps">The number of steps to turn by.</param>
        public void TurnStepper(int steps) {
            CheckReady();

            // TODO: Not sure this works, find a 9V battery and test.

            while (steps > 127) {
                SendData(new byte[] {0x54, 0xFE, 0xF0, 127});
                steps -= 127;
            }
            while (steps < -128) {
                SendData(new [] { (byte)0x54, (byte)0xFE, (byte)0xF0, (byte)(129) });
                steps += 128;
            }

            SendData(new byte[] { 0x54, 0xFE, 0xF0, (byte)steps });
        }

        #endregion

        #region Servo Motor Control

        /// <summary>
        /// Set the given servo to the position specified.
        /// </summary>
        /// <param name="servo">The servo to turn.</param>
        /// <param name="position">The position to turn to. 0 will align to center</param>
        public void SetServoPositon(Servo servo, int position) {
            CheckReady();

            // TODO: Not sure on this, I don't have a servo motor for my board.

            if (position < -90 || position > +90) {
                throw new Exception("Position should be between -90 and +90");
            }

            position += 90;
            position = (int) ((position/180.0)*255);
            position -= 127;

            SendData(new byte[] {
                0x54, 0xFE, (byte) (0xD0 | ((int) servo)), (byte) position
            });
        }

        #endregion

        #region Data sending

        private async Task<bool> SetBurstMode() {
            Task<bool> sendDataTask = SendData(new byte[] {0x54, 0xFE, 0xA0, 0xFF});
            bool result = await sendDataTask;
            return result;
        }

        private Task<bool> SendData(byte[] data) {
            try {
                _ackReceived = false;
                int retries = 0;

                while (!_ackReceived) {
                    if (retries > SenseSerialConstants.MaxRetries) {
                        throw new Exception("Device not responding?");
                    }

                    _serialPort.Write(data, 0, data.Length);
                    Thread.Sleep(100);
                    retries++;
                }

                // Is this right?
                return Task.FromResult(true);
            }
            catch (TimeoutException e) {}

            // Not sure if right way to return a successful task without async'ing
            return Task.FromResult(false);
        }

        #endregion

        #region Receving Sensor data.

        public void Decode(byte[] array) {
            Debug.Assert(array.Length == 2);

            int data = ((array[0] << 8) & 1023) | (array[1] & 0xFF);
            switch (array[0] >> 4) {
                case 0:
                    DecodeSlider(data);
                    break;
                case 1:
                    DecodeIr(data);
                    break;
                case 2:
                    DecodeSound(data);
                    break;
                case 3:
                    DecodeButton(data);
                    break;
                case 4:
                    DecodeInput(0, data);
                    break;
                case 5:
                    DecodeInput(1, data);
                    break;
                case 6:
                    DecodeInput(2, data);
                    break;
                case 7:
                    DecodeInput(3, data);
                    break;
            }
        }

        private void DecodeInput(int index, int data) {
            _resistiveInputs[index] = (int) ((data/1024.0)*100);
        }

        private void DecodeButton(int data) {
            Button = data == 0;
        }

        private void DecodeSound(int data) {
            Sound = (int) ((data/1024.0)*100);
        }

        private void DecodeIr(int data) {
            Infrared = data == 0;
        }

        private void DecodeSlider(int data) {
            _slider = (int) ((data/1024.0)*100);
        }

        #endregion

        #region Threaded Poller

        public void PollData() {
            var burstData = new byte[2];
            while (_continue) {
                try {
                    var message = (byte) _serialPort.ReadByte();


                    if (_state == SenseState.Idle && message == SenseSerialConstants.FirstAckHeader) {
                        _state = SenseState.FirstAckHeaderReceived;
                    }

                    else if (_state == SenseState.FirstAckHeaderReceived && message == SenseSerialConstants.SecondAckHeader) {
                        _state = SenseState.SecondAckHeaderReceived;
                    }

                    else if (_state == SenseState.SecondAckHeaderReceived && message == SenseSerialConstants.AckByte) {
                        _ackReceived = true;
                        _state = SenseState.Idle;
                    }
                    else if (_state == SenseState.Idle && message == SenseSerialConstants.BurstHeader) {
                        _state = SenseState.BurstHeaderRecieved;
                    }

                    else if (_state == SenseState.BurstHeaderRecieved) {
                        burstData[0] = message;
                        _state = SenseState.FirstBurstByteReceived;
                    }

                    else if (_state == SenseState.FirstBurstByteReceived) {
                        burstData[1] = message;
                        Decode(burstData);
                        _state = SenseState.Idle;
                    }
                    else {
                        _state = SenseState.Idle;
                    }
                }
                catch (TimeoutException) {}
            }
        }

        #endregion
    }
}