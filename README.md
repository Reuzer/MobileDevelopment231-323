# MobileDevelopment231-323

C# / WPF приложение для полуавтоматического выравнивания сканов:

- загрузка изображения через диалог или первый аргумент командной строки;
- анализ уменьшенной копии больших изображений;
- поиск основного контура на грязном фоне сканера;
- подсветка контура, направляющих линий и диагоналей;
- выравнивание по найденной грани;
- ручной поворот в диапазоне `-180..180`;
- центровка найденного изображения;
- экспорт результата с автообрезкой по контуру и отступом в пикселях.

## Requirements

- Windows
- .NET SDK 8.0 or newer

Install the SDK from:

https://dotnet.microsoft.com/download

After installation, restart the terminal and check:

```powershell
dotnet --info
```

## Run

```powershell
dotnet run --project src/WindowsApp/WindowsApp.csproj
```

Можно сразу передать файл:

```powershell
dotnet run --project src/WindowsApp/WindowsApp.csproj -- "C:\path\to\scan.png"
```

## Build

```powershell
dotnet build MobileDevelopment231-323.sln
```

## Hotkeys

- `Ctrl+O` - открыть изображение
- `Ctrl+S` - сохранить результат
- `Ctrl+R` - повторно найти контур
