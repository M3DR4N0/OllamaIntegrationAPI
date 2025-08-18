using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace OllamaIntegrationAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class InnerController : ControllerBase
    {
        [HttpGet("ping")]
        public IActionResult Ping()
        {
            return Ok(new { message = "Pong" });
        }
        [HttpGet("health")]
        public IActionResult HealthCheck()
        {
            return Ok(new { status = "Healthy" });
        }
        
        [HttpGet("version")]
        public IActionResult Version()
        {
            var version = typeof(InnerController).Assembly.GetName().Version?.ToString() ?? "Unknown";
            return Ok(new { version });
        }
    }
}
