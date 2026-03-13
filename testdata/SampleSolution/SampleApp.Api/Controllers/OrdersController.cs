using Microsoft.AspNetCore.Mvc;

namespace SampleApp.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok();

    [HttpGet("{id}")]
    public IActionResult GetById(int id) => Ok();

    [HttpPost]
    public IActionResult Create([FromBody] object order) => Ok();

    [HttpDelete("{id}")]
    public IActionResult Delete(int id) => Ok();
}
