using AudioSwitcher.Properties;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace AudioSwitcher {
	public class SysTrayApp: Form {
		const string controllerExePath = "EndPointController.exe";

		[STAThread]
		public static void Main(params string[] args) {
			// All this junk is to make sure that the end point controller library exe didn't get swapped for a malicious one or something
			// Probably overkill and can probably be removed 
			if ( args.Length > 0 && args[0] == "--update-md5" ) {
				if ( System.IO.File.Exists(controllerExePath) ) {
					CreateControllerExeHash(controllerExePath);
					MessageBox.Show(Resources.MESSAGE_MD5_UPDATED, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
				} else {
					MessageBox.Show(Resources.ERROR_CONTROLLER_NOT_FOUND, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
				}
			} else {
				string errorMessage = "";
				if ( !System.IO.File.Exists(controllerExePath) ) {
					errorMessage = Resources.ERROR_CONTROLLER_NOT_FOUND + "\n" + Resources.MESSAGE_APPLICATION_WILL_BE_CLOSED;
				} else if ( Settings.Default.EndPointControllerMD5CheckSumExpect == "" ) {
					errorMessage = Resources.ERROR_NO_MD5;
				} else if ( !HasCorrectHash(controllerExePath) ) {
					errorMessage = Resources.ERROR_INCORRECT_HASH + "\n" + Resources.MESSAGE_APPLICATION_WILL_BE_CLOSED;
				}

				if ( errorMessage != "" ) {
					MessageBox.Show(errorMessage, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
				} else {
					// If everything worked, this is the starting point
					Application.Run(new SysTrayApp());
					
				}
			}
		}

		private readonly NotifyIcon trayIcon;
		private readonly ContextMenu trayMenu;

		private Thread SerialThread;

		public SysTrayApp() {

			// Make and start the thread that will communicate with the arduino
			SerialThread = new Thread(InitializeSerialComms);
			SerialThread.Start();

			// Make the UI and windows icon
			// Create a simple tray menu
			trayMenu = new ContextMenu();

			// Create a tray icon
			trayIcon = new NotifyIcon();
			trayIcon.Text = "AudioSwitcher";
			trayIcon.Icon = new Icon(Resources.speaker, 40, 40);

			// Make it so we can right click the icon, and show it
			trayIcon.ContextMenu = trayMenu;
			var exitItem = new MenuItem { Text = Resources.LABEL_EXIT };
			exitItem.Click += OnExit;
			trayMenu.MenuItems.Add(exitItem);
			trayIcon.Visible = true;
		}


		#region Serial COM 

		static SerialPort chosenPort = null;
		private static bool continueRunning = true;

		private static void InitializeSerialComms() {

			while ( continueRunning ) {
				// Get a list of serial port names.
				string[] ports = SerialPort.GetPortNames().Distinct().ToArray();

				// Loop through the ports and find the one that is our arduino
				foreach ( string port in ports ) {
					Console.WriteLine("Scanning port: " + port);

					// Create a new SerialPort object with default settings.
					chosenPort = new SerialPort {
						PortName = port,
						BaudRate = 9600,
						ReadTimeout = 1500,
						WriteTimeout = 1500,
						NewLine = "\r\n",
						DtrEnable = true
					};

					try {
						chosenPort.Open();
					} catch {
						Console.WriteLine("Failed to open port");
						chosenPort = null;
						continue;
					}

					// Start the handshake 
					// This ensures we are sending and receiving data from the correct COM port 
					chosenPort.Write("Are you a headphone holder?\n");

					string response;
					try {
						response = chosenPort.ReadLine().Trim();
					} catch ( TimeoutException ) {
						Console.WriteLine("Port did not respond to handshake request");
						chosenPort = null;
						continue;
					}

					if ( response.Equals("Yes I am!") ) {
						Console.WriteLine("Successfully established handshake connection.");
						break;
					} else {
						Console.WriteLine("Unrecognized handshake response: " + response);
						chosenPort.Close();
						chosenPort = null;
						continue;
					}
				}

				// This causes an infinite loop that tries to connect to the headphone holder forever
				if ( chosenPort == null ) {
					Console.WriteLine("No headphone holders found!");
					Thread.Sleep(1000);
					InitializeSerialComms();
				} else {
					Read();
				}
			}


		}

		// This is the main method that communicates with the ardunio 
		// It periodically (4 times a second) askes the arduino if the headphones are docked and changes the audio device accordingly 
		public static void Read() {
			while ( continueRunning ) {
				try {
					chosenPort.Write("Docked?\n");
					string message = chosenPort.ReadLine();
					Console.WriteLine(message);

					// Depending on the response, call the HeadphoneEvent method with true or false
					switch ( message ) {
						case "Yes!": HeadphoneEvent(true); break;
						case "No!": HeadphoneEvent(false); break;
					}
				} catch ( TimeoutException ) {
					// These are normal, ignore them 
				} catch {
					try {
						chosenPort.Close();
					} catch { }
					InitializeSerialComms();
					break;
				}
				Thread.Sleep(250);
			}
		}

		#endregion

		// We only want to tell windows to switch devices if they get docked or undocked (not 4 times a second) so we maintain the current state
		public static bool lastDockedState = false;
		private static void HeadphoneEvent(bool isDocked) {
			if (lastDockedState != isDocked) {
				lastDockedState = isDocked;

				// Get the list of audio devices from windows
				var devices = GetDevices();
				var deviceNameSearchString = "";
				if ( isDocked ) {
					deviceNameSearchString = "[S]";
					Console.WriteLine("Switching to speakers...");
				} else {
					deviceNameSearchString = "[H]";
					Console.WriteLine("Switching to headphones...");
				}
				// You should rename your headphones to "[H] any name here"
				// and you should rename your speakers to "[S] any name here"
				// Find the devices with either [S] or [H] at the start and tell windows to switch to that device
				var deviceToSwitchTo = devices.FirstOrDefault(device => device.DeviceName.StartsWith(deviceNameSearchString));
				ChangeSoundDeviceTo(deviceToSwitchTo.DeviceID);
			}
		}

		// Everything after this point was made by other people so I don't really know how it works


		#region EndPointController.exe interaction

		struct AudioDevice {
			public int DeviceID;
			public string DeviceName;
			public bool IsActive;
			public AudioDevice(int deviceID, string deviceName, bool isActive) {
				DeviceID = deviceID;
				DeviceName = deviceName;
				IsActive = isActive;
			}
		}

		// Returns a list of devices in (ID, Name, IsActive) format
		private static IEnumerable<AudioDevice> GetDevices() {
			var devices = new List<AudioDevice>();

			if ( !System.IO.File.Exists(controllerExePath) || !HasCorrectHash(controllerExePath) ) {
				MessageBox.Show(Resources.ERROR_CONTROLLER_CHANGED + "\n" + Resources.MESSAGE_APPLICATION_WILL_BE_CLOSED, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
				Application.Exit();
			} else {
				var p = new Process {
					StartInfo = {
						UseShellExecute = false,
						RedirectStandardOutput = true,
						CreateNoWindow = true,
						FileName = controllerExePath,
						Arguments = "-f \"%d|%ws|%d|%d\""
					}
				};
				p.Start();
				p.WaitForExit();
				var stdout = p.StandardOutput.ReadToEnd().Trim();

				foreach ( var line in stdout.Split('\n') ) {
					var elems = line.Trim().Split('|');
					var deviceInfo = new AudioDevice(int.Parse(elems[0]), elems[1], elems[3].Equals("1"));
					devices.Add(deviceInfo);
				}
			}

			return devices;
		}

		private static void ChangeSoundDeviceTo(int id) {
			if ( System.IO.File.Exists(controllerExePath) && HasCorrectHash(controllerExePath) ) {
				var p = new Process {
					StartInfo = {
						UseShellExecute = false,
						RedirectStandardOutput = true,
						CreateNoWindow = true,
						FileName = controllerExePath,
						Arguments = id.ToString(CultureInfo.InvariantCulture)
					}
				};
				p.Start();
				p.WaitForExit();
			} else {
				MessageBox.Show(Resources.ERROR_CONTROLLER_CHANGED + "\n" + Resources.MESSAGE_APPLICATION_WILL_BE_CLOSED, Application.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
				Application.Exit();
			}
		}

		#endregion

		#region Main app methods

		protected override void OnLoad(EventArgs e) {
			Visible = false; // Hide form window.
			ShowInTaskbar = false; // Remove from taskbar.

			base.OnLoad(e);
		}

		private void OnExit(object sender, EventArgs e) {
			continueRunning = false;
			if (chosenPort != null ) {
				chosenPort.Close();
			}
			Application.Exit();
		}

		protected override void Dispose(bool isDisposing) {
			if ( isDisposing ) {
				// Release the icon resource.
				trayIcon.Dispose();
			}

			base.Dispose(isDisposing);
		}

		#endregion

		#region Program security: EndPointController.exe validation

		private static void CreateControllerExeHash(string controllerExePath) {
			Settings.Default.EndPointControllerMD5CheckSumExpect = ComputeMD5Checksum(controllerExePath);
			Settings.Default.Save();
		}

		private static bool HasCorrectHash(string controllerExePath) {
			return Settings.Default.EndPointControllerMD5CheckSumExpect == ComputeMD5Checksum(controllerExePath);
		}

		private static string ComputeMD5Checksum(string filePath) {
			using ( System.IO.FileStream fs = System.IO.File.OpenRead(filePath) ) {
				MD5 md5 = new MD5CryptoServiceProvider();
				byte[] fileData = new byte[fs.Length];
				fs.Read(fileData, 0, (int)fs.Length);
				byte[] checkSum = md5.ComputeHash(fileData);
				string result = BitConverter.ToString(checkSum).Replace("-", String.Empty);
				return result;
			}
		}

		#endregion
	}
}
