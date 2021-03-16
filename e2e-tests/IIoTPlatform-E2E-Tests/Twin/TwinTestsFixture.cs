﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace IIoTPlatform_E2E_Tests.Twin {
    using IIoTPlatform_E2E_Tests.TestExtensions;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;
    using RestSharp;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.Linq;
    using System.Threading;
    using Xunit;

    public class TwinTestsFixture : IIoTPlatformTestContext {
        private readonly string _endpointId;
        private readonly string _endpointUrl;

        public TwinTestsFixture() {
            (_endpointId, _endpointUrl) = GetTestServerData();
            ActivateEndpoint(_endpointId);
        }

        public new void Dispose() {         
            TestHelper.Registry_UnregisterServerAsync(this, _endpointUrl).GetAwaiter().GetResult();
            OutputHelper.WriteLine("Server endpoint unregistered");
        }

        /// <summary>
        /// Equivalent to GetSetOfUniqueNodesAsync
        /// </summary>
        /// <param name="nodeId">Id of the parent node or null to browse the root node</param>
        public List<(string NodeId, string NodeClass, bool Children)> Twin_GetBrowseEndpoint(
                string nodeId = null) {

            var result = new List<(string NodeId, string NodeClass, bool Children)>();
            string continuationToken = null;

            do {
                var browseResult = Twin_GetBrowseEndpoint_Internal(nodeId, continuationToken);

                if (browseResult.results.Count > 0) {
                    result.AddRange(browseResult.results);
                }

                continuationToken = browseResult.continuationToken;
            } while (continuationToken != null);

            return result;
        }

        /// <summary>
        /// Equivalent to recursive calling GetSetOfUniqueNodesAsync to get the whole hierarchy of nodes
        /// </summary>
        /// <param name="nodeClass">Class of the node to filter to or null for no filtering</param>
        /// <param name="nodeId">Id of the parent node or null to browse the root node</param>
        /// <param name="ct">Cancellation token</param>
        public List<(string NodeId, string NodeClass, bool Children)> Twin_GetBrowseEndpoint_Recursive(
                string nodeClass = null,
                string nodeId = null,
                CancellationToken ct = default) {

            var nodes = new ConcurrentBag<(string NodeId, string NodeClass, bool Children)>();

            Twin_GetBrowseEndpoint_RecursiveCollectResults(nodes, nodeId, ct);

            return nodes.Where(n => string.Equals(nodeClass, n.NodeClass, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// Get metadata for an OPC UA method
        /// </summary>
        /// <param name="methodId">Id of the OPC UA method</param>
        public dynamic Twin_GetMethodMetadata(
                string methodId = null) {

            var route = $"twin/v2/call/{_endpointId}/metadata";
            var body = new {
                methodId,
                header = new {
                    diagnostics = new {
                        level = "Verbose"
                    }
                }
            };
            var response = TestHelper.CallRestApi(this, Method.POST, route, body);
            return JsonConvert.DeserializeObject<ExpandoObject>(response.Content, new ExpandoObjectConverter());
        }

        /// <summary>
        /// Call OPC UA method
        /// </summary>
        /// <param name="methodId">Id of the OPC UA method</param>
        /// <param name="objectId">Context of the method, i.e. an object or object type node.</param>
        /// <param name="arguments">Arguments for the method</param>
        public dynamic Twin_CallMethod(
                string methodId,
                string objectId,
                List<object> arguments) {

            var route = $"twin/v2/call/{_endpointId}";
            var body = new {
                methodId,
                objectId,
                arguments,
                header = new {
                    diagnostics = new {
                        level = "Verbose"
                    }
                }
            };
            var response = TestHelper.CallRestApi(this, Method.POST, route, body);
            return JsonConvert.DeserializeObject<ExpandoObject>(response.Content, new ExpandoObjectConverter());
        }

        /// <summary>
        /// Calls a GET twin browse/>
        /// </summary>
        /// <param name="nodeId">Id of the parent node or null to browse the root node</param>
        /// <param name="continuationToken">Continuation token from the previous call, or null</param>
        private (List<(string NodeId, string NodeClass, bool Children)> results, string continuationToken) Twin_GetBrowseEndpoint_Internal(
                string nodeId = null,
                string continuationToken = null) {

            string route;
            var queryParams = new Dictionary<string, string>();

            if (continuationToken == null) {
                route = $"twin/v2/browse/{_endpointId}";

                if (!string.IsNullOrEmpty(nodeId)) {
                    queryParams.Add("nodeId", nodeId);
                }
            }
            else {
                route = $"twin/v2/browse/{_endpointId}/next";
                queryParams.Add("continuationToken", continuationToken);
            }

            var response = TestHelper.CallRestApi(this, Method.GET, route, queryParameters: queryParams);
            dynamic json = JsonConvert.DeserializeObject<ExpandoObject>(response.Content, new ExpandoObjectConverter());

            Assert.True(TestHelper.HasProperty(json, "references"), "GET twin/v2/browse/{endpointId} response has no items");
            Assert.False(json.references == null, "GET twin/v2/browse/{endpointId} response references property is null");

            var result = new List<(string NodeId, string NodeClass, bool Children)>();

            foreach (var node in json.references) {
                result.Add(
                    (
                        node.target?.nodeId?.ToString(),
                        node.target?.nodeClass?.ToString(),
                        string.Equals(node.target?.children?.ToString(), "true", StringComparison.OrdinalIgnoreCase)));
            }

            var responseContinuationToken = TestHelper.HasProperty(json, "continuationToken") ? json.continuationToken : null;

            return (results: result, continuationToken: responseContinuationToken);
        }

        /// <summary>
        /// Collects all nodes recursively avoiding circular references between nodes
        /// </summary>
        /// <param name="nodes">Collection of nodes found</param>
        /// <param name="nodeId">Id of the parent node or null to browse the root node</param>
        /// <param name="ct">Cancellation token</param>
        private void Twin_GetBrowseEndpoint_RecursiveCollectResults(
                ConcurrentBag<(string NodeId, string NodeClass, bool Children)> nodes,
                string nodeId = null,
                CancellationToken ct = default) {

            var currentNodes = Twin_GetBrowseEndpoint(nodeId);

            foreach (var node in currentNodes) {
                ct.ThrowIfCancellationRequested();

                if (nodes.Any(n => string.Equals(n.NodeId, node.NodeId))) {
                    continue;
                }

                nodes.Add(node);

                if (node.Children) {
                    Twin_GetBrowseEndpoint_RecursiveCollectResults(
                        nodes,
                        node.NodeId,
                        ct);
                }
            }
        }

        private (string, string) GetTestServerData() {
            var cts = new CancellationTokenSource(TestConstants.MaxTestTimeoutMilliseconds);

            // Wait for microservices of IIoT platform to be healthy and modules to be deployed.
            TestHelper.WaitForServicesAsync(this, cts.Token).GetAwaiter().GetResult();
            RegistryHelper.WaitForIIoTModulesConnectedAsync(DeviceConfig.DeviceId, cts.Token).GetAwaiter().GetResult();

            var simulatedOpcPlcs = TestHelper.GetSimulatedPublishedNodesConfigurationAsync(this, cts.Token).GetAwaiter().GetResult();
            var testPlc = simulatedOpcPlcs.Values.First();

            TestHelper.Registry_RegisterServerAsync(this, testPlc.EndpointUrl, cts.Token).GetAwaiter().GetResult();

            dynamic result = TestHelper.WaitForDiscoveryToBeCompletedAsync(this, cts.Token, new List<string> { testPlc.EndpointUrl }).GetAwaiter().GetResult();
            List<dynamic> servers = result.items;
            var server = servers.Where(s => s.discoveryUrls[0].TrimEnd('/') == testPlc.EndpointUrl).First();

            var json = TestHelper.WaitForEndpointDiscoveryToBeCompleted(this, cts.Token, new List<string> { testPlc.EndpointUrl }).GetAwaiter().GetResult();
            List<dynamic> discoveredEndpoints = json.items;
            var endpoint = discoveredEndpoints.Where(e => testPlc.EndpointUrl == (e.registration.endpointUrl).TrimEnd('/')).First();
            return (endpoint.registration.id, testPlc.EndpointUrl);
        }

        private void ActivateEndpoint(string endpointId) {
            TestHelper.Registry_ActivateEndpointAsync(this, endpointId).GetAwaiter().GetResult();
            var endpoints = TestHelper.Registry_GetEndpointsAsync(this).GetAwaiter().GetResult();
            Assert.NotEmpty(endpoints);

            var (id, url, activationState, endpointState) = endpoints.SingleOrDefault(e => string.Equals(endpointId, e.Id));
            Assert.False(id == null, "The endpoint was not found");
            Assert.Equal(TestConstants.StateConstants.ActivatedAndConnected, activationState);
            Assert.Equal(TestConstants.StateConstants.Ready, endpointState);
        }

        public dynamic Twin_GetBrowseNode(
                string nodeId = null,
                string continuationToken = null) {
            string route;
            var queryParams = new Dictionary<string, string>();

            if (continuationToken == null) {
                route = $"twin/v2/browse/{_endpointId}";

                if (!string.IsNullOrEmpty(nodeId)) {
                    queryParams.Add("nodeId", nodeId);
                }
            }
            else {
                route = $"twin/v2/browse/{_endpointId}/next";
                queryParams.Add("continuationToken", continuationToken);
            }

            var response = TestHelper.CallRestApi(this, Method.GET, route, queryParameters: queryParams);
            dynamic json = JsonConvert.DeserializeObject<ExpandoObject>(response.Content, new ExpandoObjectConverter());
            return json;
        }
    }
}
