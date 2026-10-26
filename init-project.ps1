# 1. 创建标准解决方案与目录结构
dotnet new sln -n CleanYourPC
mkdir src\CleanYourPC.Core\Models, src\CleanYourPC.Core\Services, src\CleanYourPC.App, tests\CleanYourPC.Tests, rules -Force

# 2. 将子项目加入解决方案
# (将上方对应的 .csproj、.cs、.xaml 和 rules/default-rules.json 放入对应目录后执行)
dotnet sln CleanYourPC.sln add src\CleanYourPC.Core\CleanYourPC.Core.csproj
dotnet sln CleanYourPC.sln add src\CleanYourPC.App\CleanYourPC.App.csproj
dotnet sln CleanYourPC.sln add tests\CleanYourPC.Tests\CleanYourPC.Tests.csproj

# 3. 执行沙箱单元测试并启动中文桌面端
dotnet test
dotnet run --project src\CleanYourPC.App\CleanYourPC.App.csproj