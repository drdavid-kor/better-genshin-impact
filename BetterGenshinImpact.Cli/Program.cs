using BetterGenshinImpact.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
return await new CliApplication().RunAsync(args, Console.Out, cancellation.Token);
