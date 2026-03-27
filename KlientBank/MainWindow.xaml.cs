using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace KlientBank
{
    public partial class MainWindow : Window
    {
        private TcpBankClient? _client;
        private bool _isConnected;

        public MainWindow()
        {
            InitializeComponent();
            IdTextBox.TextChanged += (_, _) => UpdateActionButtonsState();
            PhoneTextBox.TextChanged += (_, _) => UpdateActionButtonsState();
            SetConnected(false);
        }

        private void SetConnected(bool connected)
        {
            _isConnected = connected;
            UpdateActionButtonsState();
        }

        private void UpdateActionButtonsState()
        {
            bool hasId = !string.IsNullOrWhiteSpace(IdTextBox.Text);
            bool hasPhone = !string.IsNullOrWhiteSpace(PhoneTextBox.Text);

            AllClientsButton.IsEnabled = _isConnected;
            AccountsButton.IsEnabled = _isConnected && hasId;
            CardsButton.IsEnabled = _isConnected && hasId;
            TransactionsButton.IsEnabled = _isConnected && hasId;
            FindClientButton.IsEnabled = _isConnected && hasPhone;
        }

        private void SetLoading(bool isLoading)
        {
            LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            ConnectButton.IsEnabled = !isLoading;
        }

        private void ShowSuccessToast(string message)
        {
            SuccessToast.Visibility = Visibility.Visible;
            SuccessToast.Opacity = 0;

            if (SuccessToast.Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock txt)
                txt.Text = message;

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260))
            {
                BeginTime = TimeSpan.FromMilliseconds(1400)
            };
            fadeOut.Completed += (_, _) => SuccessToast.Visibility = Visibility.Collapsed;

            var sb = new Storyboard();
            Storyboard.SetTarget(fadeIn, SuccessToast);
            Storyboard.SetTargetProperty(fadeIn, new PropertyPath(Border.OpacityProperty));
            Storyboard.SetTarget(fadeOut, SuccessToast);
            Storyboard.SetTargetProperty(fadeOut, new PropertyPath(Border.OpacityProperty));
            sb.Children.Add(fadeIn);
            sb.Children.Add(fadeOut);
            sb.Begin();
        }

        private void AppendOutput(string text)
        {
            ShowTextOutput();
            OutputTextBox.AppendText(text + Environment.NewLine);
            OutputTextBox.ScrollToEnd();
        }

        private void ShowTextOutput()
        {
            OutputDataGrid.Visibility = Visibility.Collapsed;
            OutputTextBox.Visibility = Visibility.Visible;
        }

        private void ShowTableOutput(DataTable table)
        {
            OutputTextBox.Visibility = Visibility.Collapsed;
            OutputDataGrid.Visibility = Visibility.Visible;
            OutputDataGrid.ItemsSource = table.DefaultView;
        }

        private bool TryParsePipeTable(string response, out DataTable table)
        {
            table = new DataTable();
            var lines = response
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("|"))
                .ToList();

            if (lines.Count < 2)
                return false;

            string[] SplitCells(string line) =>
                line.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            var header = SplitCells(lines[0]);
            if (header.Length == 0)
                return false;

            foreach (var h in header)
                table.Columns.Add(string.IsNullOrWhiteSpace(h) ? "col" : h);

            for (int i = 1; i < lines.Count; i++)
            {
                var cells = SplitCells(lines[i]);
                if (cells.Length == 0)
                    continue;

                var row = table.NewRow();
                int limit = Math.Min(table.Columns.Count, cells.Length);
                for (int c = 0; c < limit; c++)
                    row[c] = cells[c];
                table.Rows.Add(row);
            }

            return table.Rows.Count > 0;
        }

        private static bool LooksLikeEmptyPipeTable(string response)
        {
            var lines = response
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .ToList();

            // Частый формат пустого ответа от сервера:
            // | col1 | col2 |
            // +----------------
            // (без строк данных)
            int pipeRows = lines.Count(l => l.StartsWith("|"));
            return pipeRows == 1;
        }

        private static bool TryGetNoDataMessage(string request, string response, out string message)
        {
            message = string.Empty;

            if (!string.IsNullOrWhiteSpace(response) && response.StartsWith("ERROR|", StringComparison.OrdinalIgnoreCase))
                return false;

            bool noData = string.IsNullOrWhiteSpace(response) || LooksLikeEmptyPipeTable(response);
            if (!noData)
                return false;

            string command = request.Split('|')[0];
            message = command switch
            {
                "GET_ALL_CLIENTS" => "Нет зарегистрированных клиентов",
                "GET_ACCOUNTS" => "Нет открытых счетов",
                "GET_CARDS" => "Нет зарегистрированных карт",
                "GET_TRANSACTIONS" => "Нет операций",
                "FIND_CLIENT" => "Клиент не найден",
                "GET_CLIENT" => "Клиент не найден",
                _ => "Нет данных"
            };

            return true;
        }

        private void RenderResponse(string request, string response)
        {
            if (TryGetNoDataMessage(request, response, out var noDataMessage))
            {
                ShowTextOutput();
                AppendOutput("Ответ:");
                AppendOutput(noDataMessage);
                return;
            }

            if (TryParsePipeTable(response, out var table))
            {
                ShowTableOutput(table);
                return;
            }

            ShowTextOutput();
            AppendOutput("Ответ:");
            AppendOutput(response);
        }

        private string GetIp()
        {
            var ip = IpTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(ip))
                throw new InvalidOperationException("Некорректный IP.");

            return ip;
        }

        private int GetPort()
        {
            if (!int.TryParse(PortTextBox.Text.Trim(), out int port))
                throw new InvalidOperationException("Некорректный порт.");

            if (port <= 0 || port > 65535)
                throw new InvalidOperationException("Порт должен быть в диапазоне 1..65535.");

            return port;
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            OutputTextBox.Clear();
            OutputDataGrid.ItemsSource = null;
            ShowTextOutput();
            SetConnected(false);

            try
            {
                _client = new TcpBankClient(GetIp(), GetPort());

                AppendOutput("Запрос: PING");

                SetLoading(true);
                string resp = await _client.SendRequestAsync("PING");

                RenderResponse("PING", resp);

                SetConnected(true);
                ShowSuccessToast("Подключение успешно");
            }
            catch (Exception ex)
            {
                _client = null;
                SetConnected(false);
                AppendOutput("Ошибка: " + ex.Message);
            }
            finally
            {
                SetLoading(false);
            }
        }

        private async void AllClientsButton_Click(object sender, RoutedEventArgs e)
        {
            await SendAndShowAsync("GET_ALL_CLIENTS");
        }

        private async void AccountsButton_Click(object sender, RoutedEventArgs e)
        {
            string id = IdTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(id))
                return;
            await SendAndShowAsync($"GET_ACCOUNTS|{id}");
        }

        private async void CardsButton_Click(object sender, RoutedEventArgs e)
        {
            string id = IdTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(id))
                return;
            await SendAndShowAsync($"GET_CARDS|{id}");
        }

        private async void TransactionsButton_Click(object sender, RoutedEventArgs e)
        {
            string id = IdTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(id))
                return;
            await SendAndShowAsync($"GET_TRANSACTIONS|{id}");
        }

        private async void FindClientButton_Click(object sender, RoutedEventArgs e)
        {
            string phone = PhoneTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(phone))
                return;
            await SendAndShowAsync($"FIND_CLIENT|{phone}");
        }

        private async Task SendAndShowAsync(string request)
        {
            if (_client == null)
            {
                AppendOutput("Сначала подключись (PING).");
                return;
            }

            try
            {
                AppendOutput("Запрос: " + request);

                SetLoading(true);
                string resp = await _client.SendRequestAsync(request);

                RenderResponse(request, resp);
            }
            catch (Exception ex)
            {
                AppendOutput("Ошибка: " + ex.Message);
            }
            finally
            {
                SetLoading(false);
            }
        }
    }
}