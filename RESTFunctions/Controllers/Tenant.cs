﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json.Linq;
using RESTFunctions.Models;
using RESTFunctions.Services;

namespace RESTFunctions.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class Tenant : ControllerBase
    {
        private readonly ILogger<Tenant> _logger;
        public Tenant(Graph graph, ILogger<Tenant> logger, InvitationService inviter)
        {
            _graph = graph;
            _logger = logger;
            _logger.LogInformation("Tenant ctor");
            _inviter = inviter;
        }
        Graph _graph;
        InvitationService _inviter;

        [HttpGet("oauth2")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> Get()
        {
            var id = User.FindFirst("appTenantId").Value;
            Guid guid;
            if (!Guid.TryParse(id, out guid))
                return BadRequest("Invalid id");
            var http = await _graph.GetClientAsync();
            try
            {
                var json = await http.GetStringAsync($"{Graph.BaseUrl}groups/{id}");
                var result = JObject.Parse(json);
                var tenant = new TenantDef()
                {
                    name = result["displayName"].Value<string>(),
                    description = result["description"].Value<string>(),
                };
                tenant.requireMFA = await IsMFARequired(id);
                return new JsonResult(tenant);
            } catch (HttpRequestException)
            {
                return NotFound();
            }
        }
        // Used by IEF
        // POST api/values
        [HttpPost]
        public async Task<IActionResult> Post([FromBody] TenantDef tenant)
        {
            _logger.LogDebug("Starting POST /tenant");
            //if ((User == null) || (!User.IsInRole("ief"))) return new UnauthorizedObjectResult("Unauthorized");
            if ((string.IsNullOrEmpty(tenant.name) || (string.IsNullOrEmpty(tenant.ownerId))))
                return BadRequest(new { userMessage = "Bad parameters", status = 409, version = 1.0 });

            var http = await _graph.GetClientAsync();
            try
            {
                await http.GetStringAsync($"{Graph.BaseUrl}users/{tenant.ownerId}");
            } catch (HttpRequestException ex)
            {
                return BadRequest(new { userMessage = "Bad user id", status = 409, version = 1.0 });
            }
            if ((tenant.name.Length > 60) || !Regex.IsMatch(tenant.name, "^[A-Za-z]\\w*$"))
                return BadRequest(new { userMessage = "Invalid tenant name", status = 409, version = 1.0 });
            var resp = await http.GetAsync($"{Graph.BaseUrl}groups?$filter=(displayName eq '{tenant.name}')");
            if (!resp.IsSuccessStatusCode)
                return BadRequest(new { userMessage = "Unable to validate tenant existence", status = 409, version = 1.0 });
            var values = JObject.Parse(await resp.Content.ReadAsStringAsync())["value"].Value<JArray>();
            if (values.Count != 0)
                return new ConflictObjectResult(new { userMessage = "Tenant already exists", status = 409, version = 1.0 });
            var group = new
            {
                description = tenant.description,
                mailNickname = tenant.name,
                displayName = tenant.name.ToUpper(),
                groupTypes = new string[] { },
                mailEnabled = false,
                securityEnabled = true,
            };
            var jGroup = JObject.FromObject(group);
            var owners = new string[] { $"{Graph.BaseUrl}users/{tenant.ownerId}" };
            jGroup.Add("owners@odata.bind", JArray.FromObject(owners));
            //jGroup.Add("members@odata.bind", JArray.FromObject(owners));
            //  https://docs.microsoft.com/en-us/graph/api/group-post-groups?view=graph-rest-1.0&tabs=http
            resp = await http.PostAsync(
                $"{Graph.BaseUrl}groups",
                new StringContent(jGroup.ToString(), System.Text.Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode)
                return BadRequest("Tenant creation failed");
            var json = await resp.Content.ReadAsStringAsync();
            var newGroup = JObject.Parse(json);
            var id = newGroup["id"].Value<string>();
            // add this group to the user's tenant collection
            return new OkObjectResult(new { id, roles = new string[] { "admin", "member" }, userMessage = "Tenant created" });
        }
        // POST api/values
        [HttpPut("oauth2")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> Put([FromBody] TenantDetails tenant)
        {
            var tenantId = User.FindFirstValue("appTenantId");
            if (tenantId == null) return null;
            if (string.IsNullOrEmpty(tenant.Name))
                return BadRequest("Invalid parameters");

            var http = await _graph.GetClientAsync();
            var groupUrl = $"{Graph.BaseUrl}groups/{tenantId}";
            var groupData = new
            {
                description = tenant.LongName,
                mailNickname = tenant.Name,
                displayName = tenant.Name.ToUpper()
            };
            var req = new HttpRequestMessage(HttpMethod.Patch, groupUrl)
            {
                Content = new StringContent(JObject.FromObject(groupData).ToString(), Encoding.UTF8, "application/json")
            };
            var resp = await http.SendAsync(req);
            /*
            var group = $"{{\"@odata.type\":\"microsoft.graph.openTypeExtension\",\"extensionName\":\"B2CMultiTenant\",\"isAADTenant\":{tenant.IsAADTenant},\"domain\":\"{tenant.IdPDomainName}\"}}";
            await http.PostAsync(
                $"{groupUrl}/extensions",
                new StringContent(
                    group,
                    System.Text.Encoding.UTF8,
                    "application/json"));
                    */
            if (!resp.IsSuccessStatusCode)
                return BadRequest("Update failed");
            if (tenant.requireMFA)
            {
                req = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/beta/groups/{tenantId}/extensions/MT.Props");
                resp = await http.SendAsync(req);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var body = "{\"@odata.type\": \"microsoft.graph.openTypeExtension\", \"extensionName\": \"MT.Props\", \"requireMFA\": true }";
                    req = new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/beta/groups/{tenantId}/extensions")
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    };
                    resp = await http.SendAsync(req);
                }
            } else
            {
                req = new HttpRequestMessage(HttpMethod.Delete, $"https://graph.microsoft.com/beta/groups/{tenantId}/extensions/MT.Props");
                await http.SendAsync(req); /* ignore response, be optimistic! */
            }
            return new OkObjectResult(new { tenantId, name = tenant.Name });
        }

        [HttpGet("forUser")]
        public async Task<IActionResult> GetForUser(string userId)
        {
            if ((User == null) || (!User.IsInRole("ief"))) return new UnauthorizedObjectResult("Unauthorized");
            var http = await _graph.GetClientAsync();
            try
            {
                var json = await http.GetStringAsync($"{Graph.BaseUrl}users/{userId}/memberOf");
                var groups = JObject.Parse(json)["value"].Value<JArray>();
                var membership = new
                {
                    tenantIds = new List<string>(),
                    tenants = new List<string>(),
                    roles = new List<string>()
                };
                foreach (var group in groups)
                {
                    var isGroup = group["@odata.type"].Value<string>() == "#microsoft.graph.group";
                    if (!isGroup) continue;
                    var id = group["id"].Value<string>();
                    json = await http.GetStringAsync($"{Graph.BaseUrl}groups/{id}/owners");
                    var values = JObject.Parse(json)["value"].Value<JArray>();
                    var admin = values.FirstOrDefault(u => u["id"].Value<string>() == userId);
                    membership.tenantIds.Add(group["id"].Value<string>());
                    membership.tenants.Add(group["displayName"].Value<string>());
                    membership.roles.Add(admin != null ? "admin" : "member");
                }
                if (membership.tenantIds.Count == 0)
                    return new JsonResult(new { });
                return new JsonResult(membership);
            }
            catch (HttpRequestException ex)
            {
                return BadRequest("Unable to validate user id");
            }
        }
        // IEF
        // Returns the first tenant user is a member of, otherwise error
        [HttpGet("first")]
        public async Task<IActionResult> FirstTenant(string userId)
        {
            if ((User == null) || (!User.IsInRole("ief"))) return new UnauthorizedObjectResult("Unauthorized");
            var tenants = await GetTenantsForUser(userId);
            if ((tenants == null) || (tenants.Count() == 0))
                return BadRequest(new { userMessage = "No tenants found", status = 400, version = "1.0" });
            var tenant = tenants.First();
            return new JsonResult(new
            {
                tenant.tenantId,
                name = tenant.tenantName,
                tenant.roles, // .Aggregate((a, s) => $"{a},{s}"),
                requireMFA = await IsMFARequired(tenant.tenantId),
                allTenants = tenants.Select(t => t.tenantName)  // .Aggregate((a, s) => $"{a},{s}")
            });
        }

        [HttpGet("getUserRoles")]
        public async Task<IActionResult> GetUserRolesByNameAsync(string tenantName, string userId)
        {
            if ((User == null) || (!User.IsInRole("ief"))) return new UnauthorizedObjectResult("Unauthorized");
            var http = await _graph.GetClientAsync();
            try
            {
                IEnumerable<string> roles = null;
                string tenantId = await GetTenantIdFromNameAsync(tenantName);
                if (!String.IsNullOrEmpty(tenantId))
                {
                    roles = await GetUserRolesByIdAsync(tenantId, userId);
                }
                return new JsonResult(new { tenantName, roles });
            }
            catch (HttpRequestException ex)
            {
                return BadRequest("Errors processing this request");
            }
        }
        [HttpPost("oauth2/invite")]
        [Authorize(Roles = "admin")]
        public string Invite([FromBody] InvitationDetails invite)
        {
            return _inviter.GetInvitationUrl(User, invite);
        }
        private async Task<IEnumerable<string>> GetUserRolesByIdAsync(string tenantId, string userId)
        {
            List<string> roles = new List<string>();
            if (await IsMemberAsync(tenantId, userId, true))
                roles.Add("admin");
            else if (await IsMemberAsync(tenantId, userId, false))
                roles.Add("member");
            else
                roles = null;
            return roles;
        }
        [Authorize]
        //[HttpGet("members/{tenantId}")] //TODO: tenantId may not go first as that would prevent ecluding this path from client cert requirement
        [HttpGet("oauth2/members")]
        public async Task<IActionResult> GetMembers()
        {
            Trace.WriteLine("Tenant:GetMembers");
            var tenantId = User.FindFirstValue("appTenantId");
            if (tenantId == null) return null;
            Trace.WriteLine($"Tenant:GetMembers: {tenantId}");
            var http = await _graph.GetClientAsync();
            var result = new List<Member>();
            foreach (var role in new string[] { "admin", "member" })
            {
                var entType = (role == "admin") ? "owners" : "members";
                var json = await http.GetStringAsync($"{Graph.BaseUrl}groups/{tenantId}/{entType}");
                foreach (var memb in JObject.Parse(json)["value"].Value<JArray>())
                {
                    var user = result.FirstOrDefault(m => m.userId == memb["id"].Value<string>());
                    if (user != null) // already exists; can only be because already owner; add member role
                        user.roles.Add("member");
                    else
                    {
                        user = new Member()
                        {
                            tenantId = tenantId,
                            userId = memb["id"].Value<string>(),
                            roles = new List<string>() { role }
                        };
                        var userJson = await http.GetStringAsync($"{Graph.BaseUrl}users/{user.userId}?$select=displayName,identities");
                        user.name = JObject.Parse(userJson)["displayName"].Value<string>();
                        result.Add(user);
                    }
                }
            }
            return new JsonResult(result);
        }

        private async Task<bool> IsMemberAsync(string tenantId, string userId, bool asAdmin = false)
        {
            var http = await _graph.GetClientAsync();
            var membType = asAdmin ? "owners" : "members";
            var json = await http.GetStringAsync($"{Graph.BaseUrl}groups/{tenantId}/{membType}");
            var members = JObject.Parse(json)["value"].Value<JArray>();
            var member = members.FirstOrDefault(m => m["id"].Value<string>() == userId.ToString());
            return (member != null);
        }

        // Used by IEF
        // add or confirm user is member, return roles
        [HttpPost("member")]
        public async Task<IActionResult> Member([FromBody] TenantIdMember memb)
        {
            _logger.LogTrace("Member: {0}", memb.tenantId);
            if ((User == null) || (!User.IsInRole("ief"))) return new UnauthorizedObjectResult("Unauthorized");
            var tenantId = memb.tenantId;
            _logger.LogTrace("Tenant id: {0}", tenantId);
            if (String.IsNullOrEmpty(tenantId))
                return new NotFoundObjectResult(new { userMessage = "Tenant does not exist", status = 404, version = 1.0 });
            var http = await _graph.GetClientAsync();
            string appTenantName;
            try
            {
                var json = await http.GetStringAsync($"{Graph.BaseUrl}groups/{tenantId}");
                appTenantName = JObject.Parse(json).Value<string>("displayName");
            } catch(Exception)
            {
                return new NotFoundObjectResult(new { userMessage = "Tenant does not exist", status = 404, version = 1.0 });
            }
            if (await IsMemberAsync(tenantId, memb.userId, true)) // skip already an admin
            {
                return new JsonResult(new { tenantId, tenantName = appTenantName, roles = new string[] { "admin", "member" } });
            }
            else if (await IsMemberAsync(tenantId, memb.userId, false))
            {
                return new JsonResult(new { tenantId, tenantName = appTenantName, roles = new string[] { "member" } });
            }
            else
            {
                var resp = await http.PostAsync(
                    $"{Graph.BaseUrl}groups/{tenantId}/members/$ref",
                    new StringContent(
                        $"{{\"@odata.id\": \"https://graph.microsoft.com/v1.0/directoryObjects/{memb.userId}\"}}",
                        System.Text.Encoding.UTF8,
                        "application/json"));
                if (!resp.IsSuccessStatusCode)
                    return BadRequest("Add member failed");
                return new JsonResult(new { tenantId, tenantName = appTenantName, roles = new string[] { "member" }, isNewMember = true });
            }
        }
        // Used by IEF
        [HttpPost("currmember")]
        public async Task<IActionResult> ExistingMember([FromBody] TenantMember memb)
        {
            if ((User == null) || (!User.IsInRole("ief"))) return new UnauthorizedObjectResult("Unauthorized");
            var ts = await GetTenantsForUser(memb.userId);
            var tenant = ts.FirstOrDefault(t => t.tenantName == memb.tenantName);
            if (tenant != null)
            {
                return new JsonResult(new {
                    tenant.tenantId,
                    name = tenant.tenantName,
                    requireMFA = await IsMFARequired(tenant.tenantId),
                    tenant.roles, // .Aggregate((a, s) => $"{a},{s}"),
                    allTenants = ts.Select(t => t.tenantName)  // .Aggregate((a, s) => $"{a},{s}")
                });
            }
            return new NotFoundObjectResult(new { userMessage = "User is not a member of this tenant", status = 404, version = 1.0 });
        }

        private async Task<string> GetTenantIdFromNameAsync(string tenantName)
        {
            var http = await _graph.GetClientAsync();
            var json = await http.GetStringAsync($"{Graph.BaseUrl}groups?$filter=(mailNickName eq '{tenantName.ToUpper()}')");
            var tenants = JObject.Parse(json)["value"].Value<JArray>();
            string tenantId = null;
            if (tenants.Count == 1)
            {
                tenantId = tenants[0]["id"].Value<string>();
                return tenantId;
            }
            return null;
        }
        private async Task<IEnumerable<Member>> GetTenantsForUser(string userId)
        {
            var result = new List<Member>();
            var http = await _graph.GetClientAsync();
            try
            {
                foreach (var role in new string[] { "ownedObjects", "memberOf" })
                {
                    var json = await http.GetStringAsync($"{Graph.BaseUrl}users/{userId}/{role}");
                    var groups = JObject.Parse(json)["value"].Value<JArray>();
                    foreach (var group in groups)
                    {
                        var isGroup = group["@odata.type"].Value<string>() == "#microsoft.graph.group";
                        if (!isGroup) continue;
                        var tenantId = group["id"].Value<string>();
                        var currTenant = result.FirstOrDefault(m => m.tenantId == tenantId);
                        if (currTenant != null)
                            currTenant.roles.Add(role == "ownedObjects" ? "admin" : "member");
                        else
                            result.Add(new Member()
                            {
                                tenantId = group["id"].Value<string>(),
                                tenantName = group["displayName"].Value<string>(),
                                roles = new List<string>() { role == "ownedObjects" ? "admin" : "member" },
                                userId = userId
                            });
                    }
                }
                return result;
            }
            catch (HttpRequestException ex)
            {
                return null;
            }
        }
        private async Task<bool> IsMFARequired(string id)
        {
            var http = await _graph.GetClientAsync();
            var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{Graph.BaseUrl}groups/{id}/extensions/MT.Props/"));
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                var result = JObject.Parse(json);
                try
                {
                    return result["requireMFA"].Value<bool>();
                }
                catch // we may not have all props when there are more custom props to deal with
                {
                }
            }
            return false;
        }
    }

    public class TenantDef
    {
        public string name { get; set; }
        public string description { get; set; }
        public string ownerId { get; set; }
        public bool requireMFA { get; set; }
    }
    public class TenantMember
    {
        public string tenantName { get; set; }
        public string userId { get; set; }
    }
    public class TenantIdMember
    {
        public string tenantId { get; set; }
        public string userId { get; set; }
    }
    public class Member
    {
        public string tenantId { get; set; }
        public string tenantName { get; set; }
        public string userId { get; set; }
        public List<string> roles { get; set; }
        public string name { get; set; }
    }
}
