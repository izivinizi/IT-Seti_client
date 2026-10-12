using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace ITSeti.Maintenance.App
{
    internal sealed class Win7ServerEnrollmentClient : IDisposable
    {
        private const string ServerUrl = "https://it-seti.nylenz.ru/";
        private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance");
        private static readonly string DevicePath = Path.Combine(Root, "server-device.json");
        private readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private DeviceRecord device;

        internal async Task<bool> TryAutomaticEnrollmentAsync()
        {
            device = ReadDevice();
            if (device != null) return false;
            using (var response = await PostAsync("api/v1/auto-enroll", BuildEnrollment(0, null)))
            {
                if (response.StatusCode == HttpStatusCode.Conflict || response.StatusCode == HttpStatusCode.NotFound) return false;
                await EnsureSuccessAsync(response);
                await SaveEnrollmentAsync(response);
                return true;
            }
        }

        internal async Task<CatalogState> LoadAsync()
        {
            device = ReadDevice();
            using (var catalogResponse = await SendAsync(HttpMethod.Get, "api/v1/catalog", null, device != null))
            {
                await EnsureSuccessAsync(catalogResponse);
                var catalog = json.Deserialize<CatalogResponse>(await catalogResponse.Content.ReadAsStringAsync()) ?? new CatalogResponse();
                if (device == null)
                    return new CatalogState(catalog.companies ?? new List<Company>(), catalog.sites ?? new List<Site>(), new Assignment());
                using (var assignmentResponse = await SendAsync(HttpMethod.Get, "api/v1/assignment", null, true))
                {
                    await EnsureSuccessAsync(assignmentResponse);
                    var assignment = json.Deserialize<Assignment>(await assignmentResponse.Content.ReadAsStringAsync()) ?? new Assignment();
                    return new CatalogState(catalog.companies ?? new List<Company>(), catalog.sites ?? new List<Site>(), assignment);
                }
            }
        }

        internal async Task SaveAsync(long companyId, long? siteId)
        {
            if (device == null)
            {
                using (var response = await PostAsync("api/v1/enroll", BuildEnrollment(companyId, siteId)))
                {
                    await EnsureSuccessAsync(response);
                    await SaveEnrollmentAsync(response);
                }
                return;
            }
            using (var response = await SendAsync(HttpMethod.Put, "api/v1/assignment",
                new { companyId = companyId, siteId = siteId, inventoryNumber = ReadInventory() }, true))
                await EnsureSuccessAsync(response);
            device.companyId = companyId;
            device.siteId = siteId;
            SaveDevice(device);
        }

        internal async Task AllowProcessAsync(string name, string publisher, string signature)
        {
            device = ReadDevice();
            if (device == null) throw new InvalidOperationException("Компьютер ещё не подключён к серверу.");
            using (var response = await SendAsync(HttpMethod.Post, "api/v1/process-policy/allow",
                new { name = name, publisher = publisher, signature = signature }, true))
                await EnsureSuccessAsync(response);
        }

        internal async Task TrustPublisherAsync(string publisher, string signature)
        {
            if (!string.Equals(signature, "Valid", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Издатель не подтверждён действительной цифровой подписью.");
            device = ReadDevice();
            if (device == null) throw new InvalidOperationException("Компьютер ещё не подключён к серверу.");
            using (var response = await SendAsync(HttpMethod.Post, "api/v1/process-policy/allow-publisher",
                new { publisher = publisher, signature = signature }, true))
                await EnsureSuccessAsync(response);
        }

        private object BuildEnrollment(long companyId, long? siteId)
        {
            return new
            {
                computerName = Environment.MachineName,
                serialNumber = ReadWmi("Win32_BIOS", "SerialNumber"),
                hardwareUuid = ReadWmi("Win32_ComputerSystemProduct", "UUID"),
                inventoryNumber = ReadInventory(),
                companyId = companyId,
                siteId = siteId
            };
        }

        private Task<HttpResponseMessage> PostAsync(string path, object body) => SendAsync(HttpMethod.Post, path, body, false);

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, bool authenticated)
        {
            var request = new HttpRequestMessage(method, new Uri(new Uri(ServerUrl), path));
            if (authenticated)
            {
                if (device == null) throw new InvalidOperationException("Данные подключения к серверу не найдены.");
                request.Headers.Add("X-Device-Id", device.deviceId);
                request.Headers.Add("X-Device-Key", device.deviceKey);
            }
            if (body != null) request.Content = new StringContent(json.Serialize(body), Encoding.UTF8, "application/json");
            try { return await http.SendAsync(request); }
            finally { request.Dispose(); }
        }

        private async Task SaveEnrollmentAsync(HttpResponseMessage response)
        {
            var result = json.Deserialize<EnrollmentResponse>(await response.Content.ReadAsStringAsync());
            Guid parsed;
            if (result == null || !Guid.TryParse(result.deviceId, out parsed) || string.IsNullOrWhiteSpace(result.deviceKey))
                throw new InvalidDataException("Сервер вернул неполные данные подключения.");
            device = new DeviceRecord
            {
                serverUrl = ServerUrl,
                deviceId = result.deviceId,
                deviceKey = result.deviceKey,
                supportKey = result.supportKey,
                companyId = result.companyId,
                siteId = result.siteId
            };
            SaveDevice(device);
            if (!string.IsNullOrWhiteSpace(result.supportKey))
                File.WriteAllText(Path.Combine(Root, "server-support.json"), json.Serialize(new
                {
                    serverUrl = ServerUrl,
                    deviceId = result.deviceId,
                    supportKey = result.supportKey
                }), new UTF8Encoding(false));
        }

        private async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return;
            var body = await response.Content.ReadAsStringAsync();
            try
            {
                var error = json.Deserialize<ErrorResponse>(body);
                if (error != null && !string.IsNullOrWhiteSpace(error.error)) throw new InvalidOperationException(error.error);
            }
            catch (InvalidOperationException) { throw; }
            catch { }
            throw new InvalidOperationException("Сервер вернул HTTP " + (int)response.StatusCode + " (" + response.ReasonPhrase + ").");
        }

        private DeviceRecord ReadDevice()
        {
            try
            {
                if (!File.Exists(DevicePath)) return null;
                var value = json.Deserialize<DeviceRecord>(File.ReadAllText(DevicePath));
                Guid parsed;
                Uri uri;
                return value != null && Uri.TryCreate(value.serverUrl, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
                    string.Equals(uri.Host, "it-seti.nylenz.ru", StringComparison.OrdinalIgnoreCase) &&
                    Guid.TryParse(value.deviceId, out parsed) && !string.IsNullOrWhiteSpace(value.deviceKey) ? value : null;
            }
            catch { return null; }
        }

        private void SaveDevice(DeviceRecord value)
        {
            Directory.CreateDirectory(Root);
            var temporary = DevicePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, json.Serialize(value), new UTF8Encoding(false));
            if (File.Exists(DevicePath)) File.Delete(DevicePath);
            File.Move(temporary, DevicePath);
        }

        private static string ReadInventory()
        {
            try
            {
                var path = Path.Combine(Root, "inventory.txt");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch { return null; }
        }

        private static string ReadWmi(string className, string property)
        {
            try
            {
                using (var search = new ManagementObjectSearcher("root\\cimv2", "SELECT " + property + " FROM " + className))
                using (var values = search.Get())
                    foreach (ManagementObject item in values)
                    {
                        var value = Convert.ToString(item[property]);
                        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                    }
            }
            catch { }
            return null;
        }

        public void Dispose() { http.Dispose(); }

        internal sealed class Company { public long id { get; set; } public string name { get; set; } }
        internal sealed class Site
        {
            public long id { get; set; }
            public string name { get; set; }
            public string address { get; set; }
            public long companyId { get; set; }
            public string DisplayName { get { return string.IsNullOrWhiteSpace(address) ? name : name + " · " + address; } }
        }
        internal sealed class Assignment { public long companyId { get; set; } public long? siteId { get; set; } public string company { get; set; } public string site { get; set; } }
        internal sealed class CatalogState
        {
            internal CatalogState(List<Company> companies, List<Site> sites, Assignment assignment) { Companies = companies; Sites = sites; Assignment = assignment; }
            internal List<Company> Companies { get; private set; }
            internal List<Site> Sites { get; private set; }
            internal Assignment Assignment { get; private set; }
        }
        private sealed class CatalogResponse { public List<Company> companies { get; set; } public List<Site> sites { get; set; } }
        private sealed class DeviceRecord { public string serverUrl { get; set; } public string deviceId { get; set; } public string deviceKey { get; set; } public string supportKey { get; set; } public long? companyId { get; set; } public long? siteId { get; set; } }
        private sealed class EnrollmentResponse { public string deviceId { get; set; } public string deviceKey { get; set; } public string supportKey { get; set; } public long? companyId { get; set; } public long? siteId { get; set; } }
        private sealed class ErrorResponse { public string error { get; set; } }
    }

    internal sealed class Win7ServerEnrollmentDialog : Window
    {
        private readonly Win7ServerEnrollmentClient client = new Win7ServerEnrollmentClient();
        private readonly ComboBox companyBox = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, Height = 32, DisplayMemberPath = "name" };
        private readonly ComboBox siteBox = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, Height = 32, DisplayMemberPath = "DisplayName", IsEnabled = false };
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly Button save = new Button { Content = "Сохранить", Width = 120, Height = 34, IsDefault = true, IsEnabled = false };
        private List<Win7ServerEnrollmentClient.Site> allSites = new List<Win7ServerEnrollmentClient.Site>();

        internal Win7ServerEnrollmentDialog(Window owner)
        {
            Owner = owner;
            Title = "Компания и объект ПК";
            Width = 620;
            Height = 370;
            MinWidth = 540;
            MinHeight = 330;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            Background = System.Windows.Media.Brushes.WhiteSmoke;

            var root = new Grid { Margin = new Thickness(24) };
            for (var i = 0; i < 6; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            root.Children.Add(new TextBlock { Text = "Компания и объект ПК", FontSize = 23, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.DarkBlue });
            var hint = new TextBlock { Text = "Выберите компанию. Объект можно оставить пустым.", Margin = new Thickness(0, 8, 0, 16) };
            Grid.SetRow(hint, 1); root.Children.Add(hint);
            AddField(root, "Компания", companyBox, 2);
            AddField(root, "Объект", siteBox, 3);
            Grid.SetRow(status, 4); root.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Button { Content = "Отмена", Width = 105, Height = 34, Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
            buttons.Children.Add(cancel); buttons.Children.Add(save); Grid.SetRow(buttons, 5); root.Children.Add(buttons);
            Content = root;

            Loaded += async (s, e) => await LoadAsync();
            companyBox.SelectionChanged += CompanyChanged;
            save.Click += async (s, e) => await SaveAsync();
            Closed += (s, e) => client.Dispose();
        }

        private static void AddField(Grid root, string label, Control control, int row)
        {
            var panel = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            Grid.SetColumn(control, 1); panel.Children.Add(control); Grid.SetRow(panel, row); root.Children.Add(panel);
        }

        private async Task LoadAsync()
        {
            SetBusy(true, "Получаем актуальный каталог с сервера…");
            try
            {
                var data = await client.LoadAsync();
                allSites = data.Sites.OrderBy(x => x.name).ToList();
                companyBox.ItemsSource = data.Companies.OrderBy(x => x.name).ToList();
                companyBox.SelectedItem = data.Companies.FirstOrDefault(x => x.id == data.Assignment.companyId);
                RefreshSites(data.Assignment.siteId);
                SetBusy(false, data.Assignment.companyId > 0 ? "Сервер подключён." : "Выберите компанию.");
            }
            catch (Exception ex) { SetBusy(false, ex.Message); }
        }

        private void CompanyChanged(object sender, SelectionChangedEventArgs e) { RefreshSites(null); }

        private void RefreshSites(long? selectedId)
        {
            var company = companyBox.SelectedItem as Win7ServerEnrollmentClient.Company;
            if (company == null) { siteBox.ItemsSource = null; siteBox.IsEnabled = false; save.IsEnabled = false; return; }
            var values = new List<Win7ServerEnrollmentClient.Site> { new Win7ServerEnrollmentClient.Site { id = 0, name = "Без объекта", companyId = company.id } };
            values.AddRange(allSites.Where(x => x.companyId == company.id));
            siteBox.ItemsSource = values;
            siteBox.SelectedItem = selectedId.HasValue ? values.FirstOrDefault(x => x.id == selectedId.Value) : null;
            if (siteBox.SelectedItem == null) siteBox.SelectedIndex = values.Count == 2 ? 1 : 0;
            siteBox.IsEnabled = true;
            save.IsEnabled = true;
        }

        private async Task SaveAsync()
        {
            var company = companyBox.SelectedItem as Win7ServerEnrollmentClient.Company;
            var site = siteBox.SelectedItem as Win7ServerEnrollmentClient.Site;
            if (company == null) return;
            SetBusy(true, "Сохраняем привязку…");
            try
            {
                await client.SaveAsync(company.id, site != null && site.id > 0 ? (long?)site.id : null);
                DialogResult = true;
            }
            catch (Exception ex) { SetBusy(false, ex.Message); }
        }

        private void SetBusy(bool busy, string message)
        {
            status.Text = message;
            companyBox.IsEnabled = !busy;
            siteBox.IsEnabled = !busy && companyBox.SelectedItem != null;
            save.IsEnabled = !busy && companyBox.SelectedItem != null;
        }
    }
}
