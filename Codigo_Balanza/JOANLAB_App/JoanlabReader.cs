using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace JoanlabBalanceReader
{
    public sealed class MainForm : Form
    {
        private readonly ComboBox portCombo = new ComboBox();
        private readonly ComboBox baudCombo = new ComboBox();
        private readonly Button refreshButton = new Button();
        private readonly Button connectButton = new Button();
        private readonly NumericUpDown intervalInput = new NumericUpDown();
        private readonly TextBox datasetNameInput = new TextBox();
        private readonly TextBox csvPathInput = new TextBox();
        private readonly Button samplingButton = new Button();
        private readonly Button clearButton = new Button();
        private readonly Label currentValueLabel = new Label();
        private readonly Label rawLineLabel = new Label();
        private readonly Label connectionLabel = new Label();
        private readonly DataGridView samplesGrid = new DataGridView();
        private readonly StatusStrip statusStrip = new StatusStrip();
        private readonly ToolStripStatusLabel statusLabel = new ToolStripStatusLabel();
        private readonly Timer samplingTimer = new Timer();

        private readonly object receiveLock = new object();
        private readonly object measurementLock = new object();
        private readonly StringBuilder receiveBuffer = new StringBuilder();
        private readonly Regex measurementPattern = new Regex(
            @"([-+]?\d+(?:[\.,]\d+)?)\s*([a-zA-Z\u00B5\u03BC]+)\s*$",
            RegexOptions.Compiled);

        private SerialPort serialPort;
        private bool sampling;
        private bool hasMeasurement;
        private double latestValue;
        private string latestUnit = "";
        private string latestRaw = "";
        private DateTime latestReceivedAt;
        private long sampleNumber;

        public MainForm()
        {
            Text = "Lector de balanza JOANLAB FA1204N";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(920, 600);
            Size = new Size(1080, 700);
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(245, 247, 250);

            BuildInterface();
            RefreshPorts();

            samplingTimer.Tick += SamplingTimerTick;
            FormClosing += MainFormClosing;
        }

        private void BuildInterface()
        {
            Panel connectionPanel = CreatePanel(12, 12, 1038, 64);
            Controls.Add(connectionPanel);

            AddLabel(connectionPanel, "Puerto:", 14, 12, 52);
            portCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            portCombo.SetBounds(68, 9, 105, 28);
            connectionPanel.Controls.Add(portCombo);

            refreshButton.Text = "Actualizar";
            refreshButton.SetBounds(181, 8, 88, 30);
            refreshButton.Click += delegate { RefreshPorts(); };
            connectionPanel.Controls.Add(refreshButton);

            AddLabel(connectionPanel, "Baudios:", 286, 12, 58);
            baudCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            baudCombo.Items.AddRange(new object[] { "9600", "19200", "38400", "57600", "115200" });
            baudCombo.SelectedItem = "115200";
            baudCombo.SetBounds(348, 9, 96, 28);
            connectionPanel.Controls.Add(baudCombo);

            connectButton.Text = "Conectar";
            connectButton.SetBounds(462, 8, 104, 30);
            connectButton.BackColor = Color.FromArgb(33, 150, 83);
            connectButton.ForeColor = Color.White;
            connectButton.FlatStyle = FlatStyle.Flat;
            connectButton.Click += ConnectButtonClick;
            connectionPanel.Controls.Add(connectButton);

            connectionLabel.Text = "Desconectado";
            connectionLabel.ForeColor = Color.Firebrick;
            connectionLabel.Font = new Font(Font, FontStyle.Bold);
            connectionLabel.AutoSize = true;
            connectionLabel.Location = new Point(584, 14);
            connectionPanel.Controls.Add(connectionLabel);

            Panel samplingPanel = CreatePanel(12, 86, 1038, 103);
            Controls.Add(samplingPanel);

            AddLabel(samplingPanel, "Intervalo de muestreo:", 14, 13, 145);
            intervalInput.Minimum = 100;
            intervalInput.Maximum = 3600000;
            intervalInput.Increment = 100;
            intervalInput.Value = 1000;
            intervalInput.SetBounds(162, 10, 110, 28);
            samplingPanel.Controls.Add(intervalInput);
            AddLabel(samplingPanel, "ms", 278, 13, 28);

            samplingButton.Text = "Iniciar muestreo";
            samplingButton.SetBounds(324, 9, 135, 31);
            samplingButton.Enabled = false;
            samplingButton.BackColor = Color.FromArgb(40, 105, 190);
            samplingButton.ForeColor = Color.White;
            samplingButton.FlatStyle = FlatStyle.Flat;
            samplingButton.Click += SamplingButtonClick;
            samplingPanel.Controls.Add(samplingButton);

            clearButton.Text = "Limpiar tabla";
            clearButton.SetBounds(469, 9, 105, 31);
            clearButton.Click += delegate { samplesGrid.Rows.Clear(); sampleNumber = 0; };
            samplingPanel.Controls.Add(clearButton);

            AddLabel(samplingPanel, "Nombre del set:", 594, 13, 100);
            datasetNameInput.SetBounds(697, 10, 287, 28);
            datasetNameInput.Text = "Muestreo";
            datasetNameInput.MaxLength = 80;
            datasetNameInput.TextChanged += delegate { UpdateProposedCsvPath(); };
            samplingPanel.Controls.Add(datasetNameInput);

            AddLabel(samplingPanel, "Archivo CSV:", 14, 58, 82);
            csvPathInput.SetBounds(99, 55, 885, 28);
            csvPathInput.ReadOnly = true;
            csvPathInput.BackColor = Color.White;
            samplingPanel.Controls.Add(csvPathInput);
            UpdateProposedCsvPath();

            Panel valuePanel = CreatePanel(12, 199, 1038, 105);
            Controls.Add(valuePanel);
            AddLabel(valuePanel, "Última lectura válida", 16, 12, 150);
            currentValueLabel.Text = "—";
            currentValueLabel.Font = new Font("Segoe UI", 24F, FontStyle.Bold);
            currentValueLabel.ForeColor = Color.FromArgb(31, 74, 125);
            currentValueLabel.AutoSize = true;
            currentValueLabel.Location = new Point(16, 36);
            valuePanel.Controls.Add(currentValueLabel);

            AddLabel(valuePanel, "Trama recibida:", 410, 18, 102);
            rawLineLabel.Text = "Esperando datos...";
            rawLineLabel.Font = new Font("Consolas", 11F);
            rawLineLabel.AutoEllipsis = true;
            rawLineLabel.SetBounds(410, 44, 590, 28);
            valuePanel.Controls.Add(rawLineLabel);

            samplesGrid.SetBounds(12, 314, 1038, 300);
            samplesGrid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            samplesGrid.BackgroundColor = Color.White;
            samplesGrid.BorderStyle = BorderStyle.Fixed3D;
            samplesGrid.AllowUserToAddRows = false;
            samplesGrid.AllowUserToDeleteRows = false;
            samplesGrid.ReadOnly = true;
            samplesGrid.RowHeadersVisible = false;
            samplesGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            samplesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            samplesGrid.Columns.Add("number", "N°");
            samplesGrid.Columns.Add("sampledAt", "Fecha y hora de muestra");
            samplesGrid.Columns.Add("receivedAt", "Lectura recibida");
            samplesGrid.Columns.Add("value", "Valor");
            samplesGrid.Columns.Add("unit", "Unidad");
            samplesGrid.Columns.Add("raw", "Trama original");
            samplesGrid.Columns[0].FillWeight = 35;
            samplesGrid.Columns[1].FillWeight = 130;
            samplesGrid.Columns[2].FillWeight = 90;
            samplesGrid.Columns[3].FillWeight = 65;
            samplesGrid.Columns[4].FillWeight = 45;
            samplesGrid.Columns[5].FillWeight = 170;
            Controls.Add(samplesGrid);

            statusStrip.Items.Add(statusLabel);
            statusLabel.Text = "Seleccione el puerto del ESP32-S3 y presione Conectar.";
            Controls.Add(statusStrip);
        }

        private Panel CreatePanel(int x, int y, int width, int height)
        {
            Panel panel = new Panel();
            panel.SetBounds(x, y, width, height);
            panel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            panel.BackColor = Color.White;
            panel.BorderStyle = BorderStyle.FixedSingle;
            return panel;
        }

        private static void AddLabel(Control parent, string text, int x, int y, int width)
        {
            Label label = new Label();
            label.Text = text;
            label.SetBounds(x, y, width, 24);
            label.TextAlign = ContentAlignment.MiddleLeft;
            parent.Controls.Add(label);
        }

        private void RefreshPorts()
        {
            string previous = portCombo.SelectedItem == null ? "" : portCombo.SelectedItem.ToString();
            string[] ports = SerialPort.GetPortNames();
            Array.Sort(ports, delegate(string a, string b)
            {
                int an = PortNumber(a);
                int bn = PortNumber(b);
                return an.CompareTo(bn);
            });

            portCombo.Items.Clear();
            portCombo.Items.AddRange(ports);

            if (ports.Length == 0)
            {
                statusLabel.Text = "No se encontraron puertos seriales.";
                return;
            }

            if (Array.IndexOf(ports, previous) >= 0)
                portCombo.SelectedItem = previous;
            else if (Array.IndexOf(ports, "COM25") >= 0)
                portCombo.SelectedItem = "COM25";
            else
                portCombo.SelectedIndex = 0;

            statusLabel.Text = "Puertos actualizados: " + string.Join(", ", ports);
        }

        private static int PortNumber(string port)
        {
            int result;
            if (port != null && port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(port.Substring(3), out result))
                return result;
            return int.MaxValue;
        }

        private void ConnectButtonClick(object sender, EventArgs e)
        {
            if (serialPort != null && serialPort.IsOpen)
            {
                Disconnect();
                return;
            }

            if (portCombo.SelectedItem == null)
            {
                MessageBox.Show("Seleccione un puerto serial.", "Puerto requerido",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                string portName = portCombo.SelectedItem.ToString();
                int baudRate = int.Parse(baudCombo.SelectedItem.ToString(), CultureInfo.InvariantCulture);
                serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
                serialPort.Encoding = Encoding.ASCII;
                serialPort.NewLine = "\r\n";
                serialPort.ReadTimeout = 500;
                serialPort.DataReceived += SerialDataReceived;
                serialPort.ErrorReceived += SerialErrorReceived;
                serialPort.Open();

                connectButton.Text = "Desconectar";
                connectButton.BackColor = Color.FromArgb(190, 55, 55);
                connectionLabel.Text = "Conectado a " + portName;
                connectionLabel.ForeColor = Color.FromArgb(33, 130, 70);
                samplingButton.Enabled = true;
                portCombo.Enabled = false;
                baudCombo.Enabled = false;
                refreshButton.Enabled = false;
                statusLabel.Text = "Puerto abierto. Recibiendo datos de la balanza...";
            }
            catch (Exception ex)
            {
                if (serialPort != null)
                {
                    serialPort.Dispose();
                    serialPort = null;
                }
                MessageBox.Show("No se pudo abrir el puerto:\r\n\r\n" + ex.Message,
                    "Error de conexión", MessageBoxButtons.OK, MessageBoxIcon.Error);
                statusLabel.Text = "Error al abrir el puerto.";
            }
        }

        private void Disconnect()
        {
            StopSampling();
            try
            {
                if (serialPort != null)
                {
                    serialPort.DataReceived -= SerialDataReceived;
                    serialPort.ErrorReceived -= SerialErrorReceived;
                    if (serialPort.IsOpen)
                        serialPort.Close();
                    serialPort.Dispose();
                }
            }
            catch { }
            finally
            {
                serialPort = null;
                connectButton.Text = "Conectar";
                connectButton.BackColor = Color.FromArgb(33, 150, 83);
                connectionLabel.Text = "Desconectado";
                connectionLabel.ForeColor = Color.Firebrick;
                samplingButton.Enabled = false;
                portCombo.Enabled = true;
                baudCombo.Enabled = true;
                refreshButton.Enabled = true;
                statusLabel.Text = "Puerto desconectado.";
            }
        }

        private void SerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                string chunk = serialPort.ReadExisting();
                if (chunk.Length == 0)
                    return;

                List<string> completeLines = new List<string>();
                lock (receiveLock)
                {
                    receiveBuffer.Append(chunk);
                    string data = receiveBuffer.ToString();
                    int start = 0;
                    for (int i = 0; i < data.Length; i++)
                    {
                        if (data[i] == '\r' || data[i] == '\n')
                        {
                            if (i > start)
                                completeLines.Add(data.Substring(start, i - start));
                            while (i + 1 < data.Length && (data[i + 1] == '\r' || data[i + 1] == '\n'))
                                i++;
                            start = i + 1;
                        }
                    }

                    receiveBuffer.Clear();
                    if (start < data.Length)
                        receiveBuffer.Append(data.Substring(start));
                }

                foreach (string line in completeLines)
                    ProcessLine(line.Trim());
            }
            catch (Exception ex)
            {
                SetStatusFromWorker("Error de lectura: " + ex.Message);
            }
        }

        private void ProcessLine(string line)
        {
            if (line.Length == 0)
                return;

            Match match = measurementPattern.Match(line);
            if (!match.Success)
            {
                UpdateRawLine(line);
                return;
            }

            string numberText = match.Groups[1].Value.Replace(',', '.');
            double value;
            if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                UpdateRawLine(line);
                return;
            }

            string unit = match.Groups[2].Value;
            DateTime receivedAt = DateTime.Now;
            lock (measurementLock)
            {
                latestValue = value;
                latestUnit = unit;
                latestRaw = line;
                latestReceivedAt = receivedAt;
                hasMeasurement = true;
            }

            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    currentValueLabel.Text = value.ToString("0.###", CultureInfo.InvariantCulture) + " " + unit;
                    rawLineLabel.Text = line;
                    statusLabel.Text = "Última trama válida: " + receivedAt.ToString("HH:mm:ss.fff");
                });
            }
        }

        private void UpdateRawLine(string line)
        {
            if (!IsDisposed && IsHandleCreated)
            {
                BeginInvoke((MethodInvoker)delegate { rawLineLabel.Text = line; });
            }
        }

        private void SerialErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            SetStatusFromWorker("Error serial: " + e.EventType.ToString());
        }

        private void SetStatusFromWorker(string message)
        {
            if (!IsDisposed && IsHandleCreated)
                BeginInvoke((MethodInvoker)delegate { statusLabel.Text = message; });
        }

        private void SamplingButtonClick(object sender, EventArgs e)
        {
            if (sampling)
            {
                StopSampling();
                return;
            }

            try
            {
                string datasetName = datasetNameInput.Text.Trim();
                if (datasetName.Length == 0)
                {
                    MessageBox.Show("Ingrese un nombre para el set de datos.", "Nombre requerido",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    datasetNameInput.Focus();
                    return;
                }

                string path = BuildDatasetPath(datasetName, DateTime.Now);
                string directory = Path.GetDirectoryName(path);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    File.WriteAllText(path, "set_datos,numero,fecha_hora_muestra,fecha_hora_recepcion,valor,unidad,trama_original\r\n", new UTF8Encoding(true));

                sampleNumber = 0;
                samplesGrid.Rows.Clear();
                csvPathInput.Text = path;
                intervalInput.Enabled = false;
                datasetNameInput.Enabled = false;
                csvPathInput.Enabled = false;
                samplingTimer.Interval = (int)intervalInput.Value;
                samplingTimer.Start();
                sampling = true;
                samplingButton.Text = "Detener muestreo";
                samplingButton.BackColor = Color.FromArgb(210, 120, 35);
                statusLabel.Text = "Muestreo activo cada " + intervalInput.Value.ToString(CultureInfo.InvariantCulture) + " ms.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo preparar el archivo CSV:\r\n\r\n" + ex.Message,
                    "Error de archivo", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopSampling()
        {
            samplingTimer.Stop();
            sampling = false;
            samplingButton.Text = "Iniciar muestreo";
            samplingButton.BackColor = Color.FromArgb(40, 105, 190);
            intervalInput.Enabled = true;
            datasetNameInput.Enabled = true;
            csvPathInput.Enabled = true;
            if (datasetNameInput.Text.Trim().Length > 0)
                UpdateProposedCsvPath();
        }

        private void SamplingTimerTick(object sender, EventArgs e)
        {
            double value;
            string unit;
            string raw;
            DateTime receivedAt;

            lock (measurementLock)
            {
                if (!hasMeasurement)
                {
                    statusLabel.Text = "Muestreo activo; todavía no hay una lectura válida.";
                    return;
                }
                value = latestValue;
                unit = latestUnit;
                raw = latestRaw;
                receivedAt = latestReceivedAt;
            }

            DateTime sampledAt = DateTime.Now;
            sampleNumber++;
            string valueText = value.ToString("0.###", CultureInfo.InvariantCulture);
            samplesGrid.Rows.Add(
                sampleNumber.ToString(CultureInfo.InvariantCulture),
                sampledAt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                receivedAt.ToString("HH:mm:ss.fff"),
                valueText,
                unit,
                raw);

            if (samplesGrid.Rows.Count > 0)
                samplesGrid.FirstDisplayedScrollingRowIndex = samplesGrid.Rows.Count - 1;

            try
            {
                string csvLine = string.Join(",", new string[]
                {
                    Csv(datasetNameInput.Text.Trim()),
                    sampleNumber.ToString(CultureInfo.InvariantCulture),
                    Csv(sampledAt.ToString("yyyy-MM-dd HH:mm:ss.fff")),
                    Csv(receivedAt.ToString("yyyy-MM-dd HH:mm:ss.fff")),
                    valueText,
                    Csv(unit),
                    Csv(raw)
                });
                File.AppendAllText(csvPathInput.Text, csvLine + "\r\n", Encoding.UTF8);
                statusLabel.Text = "Muestra " + sampleNumber.ToString(CultureInfo.InvariantCulture) + " guardada.";
            }
            catch (Exception ex)
            {
                StopSampling();
                MessageBox.Show("Se detuvo el muestreo porque no se pudo escribir el CSV:\r\n\r\n" + ex.Message,
                    "Error de archivo", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string Csv(string value)
        {
            if (value == null)
                return "";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private void UpdateProposedCsvPath()
        {
            if (sampling)
                return;

            string name = datasetNameInput.Text.Trim();
            if (name.Length == 0)
                name = "Muestreo";
            csvPathInput.Text = BuildDatasetPath(name, DateTime.Now);
        }

        private static string BuildDatasetPath(string datasetName, DateTime timestamp)
        {
            string safeName = SanitizeFileName(datasetName);
            return Path.Combine(
                GetDataDirectory(),
                safeName + "_" + timestamp.ToString("yyyyMMdd_HHmmss") + ".csv");
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder safe = new StringBuilder();
            foreach (char character in name.Trim())
            {
                safe.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
            }

            string result = safe.ToString().Trim().TrimEnd('.');
            return result.Length == 0 ? "Muestreo" : result;
        }

        private static string GetDataDirectory()
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Datos");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private void MainFormClosing(object sender, FormClosingEventArgs e)
        {
            Disconnect();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
