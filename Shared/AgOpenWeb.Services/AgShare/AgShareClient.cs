// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using AgOpenWeb.Models.AgShare;

namespace AgOpenWeb.Services.AgShare
{
    /// <summary>
    /// HTTP client for communicating with the AgShare API using API key authentication.
    /// Supports field upload, download, status checks, and querying both public and own fields.
    /// </summary>
    public class AgShareClient
    {
        private readonly HttpClient _client;
        private string _baseUrl;
        private string _apiKey;

        /// <summary>
        /// Constructs client with base URL and API key
        /// </summary>
        public AgShareClient(string serverUrl, string key)
        {
            _baseUrl = serverUrl.TrimEnd('/');
            _apiKey = key;
            _client = new HttpClient();
            _client.DefaultRequestHeaders.Accept.Clear();
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", _apiKey);
            _client.Timeout = TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// Updates the API key
        /// </summary>
        public void SetApiKey(string key)
        {
            _apiKey = key;
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", _apiKey);
        }

        /// <summary>
        /// Updates the base URL
        /// </summary>
        public void SetBaseUrl(string url)
        {
            _baseUrl = url.TrimEnd('/');
        }

        /// <summary>
        /// Checks if the API key and connection are valid
        /// </summary>
        public async Task<(bool ok, string message)> CheckApiAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", _apiKey);

                string requestUrl = $"{_baseUrl}/api/fields";
                var response = await _client.GetAsync(requestUrl, cts.Token);
                string responseBody = await response.Content.ReadAsStringAsync(cts.Token);

                if (response.IsSuccessStatusCode)
                    return (true, "Connection OK");
                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return (false, "Invalid API key");
                else
                    return (false, $"Status {response.StatusCode}: {responseBody}");

            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Uploads a field by ID with JSON payload
        /// </summary>
        public async Task<(bool ok, string message)> UploadFieldAsync(Guid fieldId, object fieldPayload)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var json = JsonConvert.SerializeObject(fieldPayload, Formatting.Indented);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _client.PutAsync($"{_baseUrl}/api/fields/{fieldId}", content, cts.Token);

                if (response.IsSuccessStatusCode)
                    return (true, "Upload successful");
                else
                    return (false, $"Upload failed: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                return (false, $"Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieves a list of fields owned by the current user
        /// </summary>
        public async Task<List<AgShareGetOwnFieldDto>> GetOwnFieldsAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var url = $"{_baseUrl}/api/fields/";
                var response = await _client.GetAsync(url, cts.Token);
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync(cts.Token);
                return JsonConvert.DeserializeObject<List<AgShareGetOwnFieldDto>>(json) ?? [];
            }
            catch
            {
                return [];
            }

        }

        /// <summary>
        /// Downloads a specific field as raw JSON string
        /// </summary>
        public async Task<string> DownloadFieldAsync(Guid fieldId)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var response = await _client.GetAsync($"{_baseUrl}/api/fields/{fieldId}", cts.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cts.Token);
            }
            catch
            {
                return string.Empty;
            }

        }

        /// <summary>
        /// Queries public fields within a given radius around a lat/lon
        /// </summary>
        public async Task<string> GetPublicFieldsAsync(double lat, double lon, double radius = 50)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var url = $"{_baseUrl}/api/fields/public?lat={lat}&lon={lon}&radius={radius}";
                var response = await _client.GetAsync(url, cts.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cts.Token);
            }
            catch
            {
                return string.Empty;
            }

        }
    }
}
