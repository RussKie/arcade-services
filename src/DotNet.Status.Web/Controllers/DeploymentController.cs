// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Status.Web.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.DotNet.Services.Utility;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DotNet.Status.Web.Controllers
{
    [ApiController]
    [Route("api/deployment")]
    public class DeploymentController : ControllerBase
    {
        private readonly IHostEnvironment _env;
        private readonly ExponentialRetry _retry;
        private readonly IOptionsMonitor<GrafanaOptions> _grafanaOptions;
        private readonly ILogger<DeploymentController> _logger;

        public DeploymentController(
            ExponentialRetry retry,
            IOptionsMonitor<GrafanaOptions> grafanaOptions,
            ILogger<DeploymentController> logger,
            IHostEnvironment env)
        {
            _retry = retry;
            _grafanaOptions = grafanaOptions;
            _logger = logger;
            _env = env;
        }

        [HttpPost("{service}/{id}/start")]
        public async Task<IActionResult> MarkStart([Required] string service, [Required] string id)
        {
            _logger.LogInformation("Recording start of deployment of '{service}' with id '{id}'", service, id);
            NewGrafanaAnnotationRequest content = new NewGrafanaAnnotationRequest
            {
                Text = $"Deployment of {service}",
                Tags = new[] {"deploy", $"deploy-{service}", service},
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            
            NewGrafanaAnnotationResponse annotation;
            using (var client = new HttpClient(new HttpClientHandler() { CheckCertificateRevocationList = true }))
            {
                annotation = await _retry.RetryAsync(async () =>
                    {
                        GrafanaOptions grafanaOptions = _grafanaOptions.CurrentValue;
                        _logger.LogInformation("Creating annotation to {url}", grafanaOptions.BaseUrl);
                        using (var request = new HttpRequestMessage(HttpMethod.Post,
                            $"{grafanaOptions.BaseUrl}/api/annotations"))
                        {
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                            request.Headers.Authorization =
                                new AuthenticationHeaderValue("Bearer", grafanaOptions.ApiToken);
                            request.Content = CreateObjectContent(content);

                            using (HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None))
                            {
                                _logger.LogTrace("Response from grafana {responseCode} {reason}", response.StatusCode, response.ReasonPhrase);
                                response.EnsureSuccessStatusCode();
                                return await ReadObjectContent<NewGrafanaAnnotationResponse>(response.Content);
                            }
                        }
                    },
                    e => _logger.LogWarning(e, "Failed to send new annotation"),
                    e => true
                );
            }
            _logger.LogInformation("Created annotation {annotationId}, inserting into table", annotation.Id);

            CloudTable table = await GetCloudTable();
            await table.ExecuteAsync(
                TableOperation.InsertOrReplace(
                    new AnnotationEntity(service, id, annotation.Id)
                    {
                        ETag = "*"
                    }
                )
            );
            return NoContent();
        }

        [HttpPost("{service}/{id}/end")]
        public async Task<IActionResult> MarkEnd([Required] string service, [Required] string id)
        {
            _logger.LogInformation("Recording end of deployment of '{service}' with id '{id}'", service, id);
            CloudTable table = await GetCloudTable();
            _logger.LogInformation("Looking for existing deployment");
            var tableResult = await table.ExecuteAsync(TableOperation.Retrieve<AnnotationEntity>(service, id));
            _logger.LogTrace("Table response code {responseCode}", tableResult.HttpStatusCode);
            if (!(tableResult.Result is AnnotationEntity annotation))
            {
                return NotFound();
            }

            _logger.LogTrace("Updating end time of deployment...");
            annotation.Ended = DateTimeOffset.UtcNow;
            tableResult = await table.ExecuteAsync(TableOperation.Replace(annotation));
            _logger.LogInformation("Update response code {responseCode}", tableResult.HttpStatusCode);
            
            var content = new NewGrafanaAnnotationRequest
            {
                TimeEnd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            using (var client = new HttpClient(new HttpClientHandler() { CheckCertificateRevocationList = true }))
            {
                await _retry.RetryAsync(async () =>
                    {
                        GrafanaOptions grafanaOptions = _grafanaOptions.CurrentValue;
                        _logger.LogInformation("Updating annotation {annotationId} to {url}", annotation.GrafanaAnnotationId, grafanaOptions.BaseUrl);
                        using (var request = new HttpRequestMessage(HttpMethod.Patch,
                            $"{grafanaOptions.BaseUrl}/api/annotations/{annotation.GrafanaAnnotationId}"))
                        {
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                            request.Headers.Authorization =
                                new AuthenticationHeaderValue("Bearer", grafanaOptions.ApiToken);
                            request.Content = CreateObjectContent(content);
                            using (HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None))
                            {
                                _logger.LogTrace("Response from grafana {responseCode} {reason}", response.StatusCode, response.ReasonPhrase);
                                response.EnsureSuccessStatusCode();
                            }
                        }
                    },
                    e => _logger.LogWarning(e, "Failed to send new annotation"),
                    e => true
                );
            }

            return NoContent();
        }

        private async Task<CloudTable> GetCloudTable()
        {
            CloudTable table;
            if (_env.IsDevelopment())
            {
                table = CloudStorageAccount.DevelopmentStorageAccount.CreateCloudTableClient().GetTableReference("deployments");
                await table.CreateIfNotExistsAsync();
            }
            else
            {
                GrafanaOptions options = _grafanaOptions.CurrentValue;
                table = new CloudTable(new Uri(options.TableUri, UriKind.Absolute));
            }
            return table;
        }

        private static StringContent CreateObjectContent(object content)
        {
            StringWriter writer = new StringWriter();
            s_grafanaSerializer.Serialize(writer, content);
            return new StringContent(writer.ToString(), Encoding.UTF8, "application/json");
        }

        private static async Task<T> ReadObjectContent<T>(HttpContent content)
        {
            using (var streamReader = new StreamReader(await content.ReadAsStreamAsync()))
            using (var jsonReader = new JsonTextReader(streamReader))
            {
                return s_grafanaSerializer.Deserialize<T>(jsonReader);
            }
        }

        private static readonly JsonSerializer s_grafanaSerializer = new JsonSerializer
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            Formatting = Formatting.None,
        };
    }

    public class DeploymentStartRequest
    {
        [Required]
        public string Service { get; set; }
    }

    public class NewGrafanaAnnotationRequest
    {
        public int? DashboardId { get; set; }
        public int? PanelId { get; set; }
        public long? Time { get; set; }
        public long? TimeEnd { get; set; }
        public string[] Tags { get; set; }
        public string Text { get; set; }
    }

    public class NewGrafanaAnnotationResponse
    {
        public string Message { get; set; }
        public int Id { get; set; }
    }
}
