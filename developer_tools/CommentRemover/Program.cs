using System;
using FvsComments;

if (args.Length > 0)
{
    Console.Error.WriteLine("Find_and_Remove_Comments takes no arguments. Run it as is: it lists every comment and extra blank line it would remove from the repository's code files, asks Y/N, then exits 0 (done or nothing to do), 1 (refused, nothing written) or 2 (you answered N, nothing written).");
    return 1;
}

string? root = Runner.FindRoot(Environment.CurrentDirectory) ?? Runner.FindRoot(AppContext.BaseDirectory);
if (root is null)
{
    Console.Error.WriteLine("Find_and_Remove_Comments: no build/sentinels.txt at or above the current folder.");
    return 1;
}

try
{
    return Runner.Run(root, Console.Out, AskYesNo);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"Find_and_Remove_Comments: {ex.Message} Nothing was written.");
    return 1;
}

static bool AskYesNo()
{
    while (true)
    {
        Console.WriteLine();
        Console.Write("Remove ALL the comments listed above?  Y = remove them   N = cancel, nothing is changed  [Y/N]: ");
        string? answer = Console.ReadLine();
        if (answer is null)
        {
            Console.WriteLine();
            return false;
        }
        answer = answer.Trim();
        if (answer.Equals("Y", StringComparison.OrdinalIgnoreCase) || answer.Equals("YES", StringComparison.OrdinalIgnoreCase)) return true;
        if (answer.Equals("N", StringComparison.OrdinalIgnoreCase) || answer.Equals("NO", StringComparison.OrdinalIgnoreCase)) return false;
        Console.WriteLine("Please type Y or N.");
    }
}
