using Microsoft.AspNetCore.Mvc;
using Research.Application;
using Research.Infrastructure;
namespace Research.Api;
[ApiController, Route("orders")]
public class OrdersController : ControllerBase
{
    [HttpGet] public void Get() => Service.Load();
    [HttpGet("caught")] public void Caught() => Service.Caught();
    [HttpGet("unrelated")] public void Unrelated() => Service.Unrelated();
    [HttpGet("incompatible")] public void Incompatible() => Service.Incompatible();
    [HttpGet("rethrow")] public void Rethrow() => Service.Rethrow();
    [HttpGet("replace")] public void Replace() => Service.Replace();
    [HttpGet("filter")] public void Filter(bool enabled) => Service.Filter(enabled);
    [HttpGet("interface")] public void Interface(IRepository repository) => Service.Interface(repository);
    [HttpGet("recursive")] public void Recursive() => Service.Recursive();
    [HttpGet("await")] public async Task AwaitCaught() => await Service.AwaitCaught();
    [HttpGet("unobserved")] public void Unobserved() => Service.Unobserved();
    [HttpGet("nested")] public void Nested() => Service.Nested();
    [NonAction] public void Helper() => Service.Load();
}
