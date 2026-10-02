# Tuya Control — MD3 (preview.7)

## Duża zmiana od ostatniej wersji

Ten plugin został przepisany pod **SDK 3.0.0-preview.7** (beta.24 hosta) po tym jak
`IVariableProvider` zostało w praniu przebudowane w SDK (`ProvidedVariables`/
`GetValueAsync` → `Variables`/`ReadAsync`) i `ISliderActionDefinition` zniknęło
całkowicie. Przy okazji, na wyraźną prośbę: **usunąłem całą synchronizację stanu**
(`TuyaStatusPollingService`, zmienną `device_<id>_state`, optymistyczne aktualizacje
w Toggle/SetSwitchState). Plugin wrócił do kształtu bliższego oryginałowi z MD2 —
same akcje, bez zmiennych.

## Jak to wstawić do prawdziwego projektu

1. Wygeneruj świeży projekt **z najnowszego szablonu** (ten sam co dał Ci realny
   `manifest.json`/`Directory.Packages.props` — jeśli masz już taki wygenerowany,
   użyj go jako bazy zamiast `dotnet new` od zera)
2. W korzeniu solution (obok pliku `.slnx`) muszą być: `Directory.Build.props`,
   `Directory.Packages.props`, `NuGet.config` — są w folderze `_solution-root-files/`
   w tej paczce, skopiuj je tam
3. Podmień `manifest.json`, `macrodeck-build.json`, `Program.cs`, `PluginIntegration.cs`
4. Dorzuć `Services/`, `ConfigFlow/`, `Actions/`, `Models/`, `Assets/icon.svg`
5. **Nadal aktualne**: `dotnet add package Newtonsoft.Json` — cała logika chmury/LAN
   używa `JObject`/`JArray`/`JToken`, tego pakietu nie ma domyślnie w szablonie
6. `dotnet restore` — powinno teraz ściągnąć preview.7 (dzięki `Directory.Packages.props`
   z pływającą wersją `3.0.0-*`), nie starą preview.3 z cache

## Co się zmieniło w kodzie pod preview.7

- **Logowanie**: wszędzie `Serilog.ILogger` (przez `.ForContext<T>()`) zamiast
  `Microsoft.Extensions.Logging.ILogger<T>` — nowa oficjalna wytyczna z `AGENTS.md`
  dołączonego do szablonu
- **`IActionDefinition.Name`/`Description`**: teraz `LocalizedText`, nie `string` —
  literały tekstowe kompilują się bez zmian dzięki niejawnej konwersji, zmieniłem
  tylko deklarowany typ w każdej z 4 akcji
- **`manifest.json`**: `"publisher": {"name": "Tabsik30"}` zamiast `"author"` (które
  nigdy nie działało), plus `$schema`, `compatibility.macroDeck`
- **Pakowanie**: `entrypoints.win-x64.executable` teraz wskazuje na
  `runtimes/win-x64/TuyaControl.exe` (podfolder, nie plik w korzeniu), i domyślnie
  `--self-contained true` w `macrodeck-build.json` (wcześniej `false`)

## Co zostało usunięte na Twoją prośbę

- `Services/TuyaStatusPollingService.cs` — całkowicie skasowany
- `TuyaDeviceStore`: zniknęły `_state`/`_reachable`/`GetState`/`SetState`/`IsReachable`,
  a przy okazji też cały lokalny plik-cache (`LoadLocalCache`/`SaveLocalCache`) — bez
  zmiennych do zadeklarowania, nie ma już problemu "deklaracja zanim urządzenia się
  wczytają", więc ten hack stał się zbędny
- `TuyaDeviceRecord.PrimaryCode` i pole "DP code for two-state buttons" w kroku
  "Network" configu — istniało wyłącznie na potrzeby synchronizacji stanu
- Optymistyczne `store.SetState(...)` w `ToggleSwitchAction`/`SetSwitchStateAction`

Jeśli kiedyś zechcesz to jednak mieć z powrotem — w SDK preview.7 jest nowy,
natywny `IStateProviderActionDefinition` (`GetActionStateAsync` → aktywny stan z
listy), który wygląda jak właściwy mechanizm do dwustanowych przycisków bez
osobnej zmiennej i pollingu. Nie zaimplementowałem go teraz (usunięto na wyraźną
prośbę), ale to naturalny kandydat na przyszłość.

## Co jest pewne (przeniesione 1:1 z działającego kodu MD2)

- Cały protokół LAN 3.3/3.4 (`TuyaLocalClient.cs`) i 3.5 AES-GCM (`TuyaLocalClientV35.cs`)
  — ramkowanie pakietów, CRC32, negocjacja klucza sesji, PKCS7 — bez zmian logiki
- UDP discovery (`TuyaDiscovery.cs`) — porty 6666/6667, stały klucz AES do dekodowania
  pakietów z 6667
- Logowanie do chmury i podpisywanie żądań HMAC-SHA256 (`TuyaCloudClient.cs`)
- 4 akcje: Toggle, Set fixed state, Send custom command, Detect (diagnostyka UDP)

## Client ID/Secret podawane w konfiguracji, nie w kodzie

Każdy wpisuje **własny** Client ID (Access ID) i Client Secret (Access Secret) w
kroku "Tuya cloud account" config flow. Dane bierze się z panelu iot.tuya.com →
Cloud → Development → (Twój projekt) → Overview.

## Największa zmiana architektoniczna (bez zmian od poprzedniej wersji)

`DeviceManagerForm` (tabelka WinForms z edycją IP/wersji/local key) → **jeden wpis
configu na jedno urządzenie**, przez wieloetapowy `IConfigFlow`
(`ConfigFlow/TuyaConfigFlow.cs`):

```
Krok "kind" → [Konto chmurowe] / [Urządzenie]
  Konto: region/kraj/login/hasło/Client ID/Client Secret → loguje się i cache'uje
         listę urządzeń w pamięci (TuyaCloudClient.CachedDevices) na czas życia
         procesu pluginu
  Urządzenie:
    "source" (tylko gdy jest cache z chmury): [Z chmury] / [Ręcznie]
      Z chmury: wybór z listy → auto-uzupełnia id/nazwę/local key
      Ręcznie: id/nazwa/local key wpisywane ręcznie
    "network" (zawsze): IP + wersja protokołu → zapis
```

**Wciąż nieprzetestowane na żywo pod preview.7** — testowaliśmy tylko pod preview.3
przed tą migracją. Sprawdź od nowa całą ścieżkę configu.

## Test

Po buildzie/instalacji: dodaj konto chmurowe przez config flow, sprawdź czy lista
urządzeń w kroku "Z chmury" się pojawia, dodaj jedno urządzenie, spróbuj akcji Toggle
na nim.
