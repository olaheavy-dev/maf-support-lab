# Records docs/assets/demo.gif with VHS (https://github.com/charmbracelet/vhs).
# First start the .NET backend in fake mode (no key, no cost), then render from the repository root:
#   ModelProvider__Mode=Fake dotnet run --project src/dotnet/Support.Api --launch-profile http
#   vhs docs/assets/demo.tape
# The environment override matters only if local user secrets select live mode.

Output docs/assets/demo.gif

Set Shell "bash"
Set FontSize 14
Set Width 1600
Set Height 680
Set Padding 20
Set Theme "Catppuccin Mocha"
Set TypingSpeed 40ms

Hide
Type "export PS1='$ '" Enter
Type "clear" Enter
Show

Type "scripts/demo.sh http://localhost:5180"
Sleep 600ms
Enter
Sleep 18s
