using Pole.Functions.Sdk;

var builder = WebApplication.CreateBuilder(args);

var functions = builder.Services.AddPoleFunctions(
    builder.Configuration.GetSection(PoleFunctionsOptions.SectionName));

if (builder.Environment.IsDevelopment())
{
    builder.Services.PostConfigure<PoleFunctionsOptions>(options =>
    {
        if (string.IsNullOrWhiteSpace(options.FunctionAppId))
            options.FunctionAppId = "sample-app";
        if (string.IsNullOrWhiteSpace(options.RevisionId))
            options.RevisionId = "local";
        options.AllowUnauthenticatedAgentRequests = true;
    });
}

functions
    .AddHttpHandler(
        "hello",
        (request, context, cancellationToken) =>
        {
            var name = request.QueryString.TrimStart('?');
            var message = string.IsNullOrWhiteSpace(name)
                ? "Hello from Pole Functions!"
                : $"Hello, {name}!";
            return ValueTask.FromResult(PoleHttpResponse.Text(message));
        })
    .AddTimerHandler(
        "heartbeat",
        (timer, context, cancellationToken) =>
        {
            Console.WriteLine(
                "Heartbeat {OccurrenceId} scheduled for {ScheduledForUtc:O}",
                timer.OccurrenceId,
                timer.ScheduledForUtc);
            return ValueTask.CompletedTask;
        });

var app = builder.Build();
app.MapPoleFunctionsAgent();
app.Run();
