namespace Generator.Web.Api;

/// Trzy sytuacje, ktore kontrakt kaze zamienic na 409, a nie na 500.
/// Osobne typy, bo warstwa HTTP i warstwa Blazora reaguja na nie inaczej.
public class ProjectFrozenException(string id)
    : InvalidOperationException($"projekt {id} jest zamrozony (kontrakt 4.5)");

public class StaleVersionException(int wyslana, int biezaca)
    : InvalidOperationException(
        $"uwagi dotycza wersji {wyslana}, a biezaca jest {biezaca} (kontrakt 5.1)");

public class JobRunningException(string id)
    : InvalidOperationException($"projekt {id} ma juz trwajace zadanie (plan, sekcja 7)");
