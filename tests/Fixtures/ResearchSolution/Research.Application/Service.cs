using Research.Infrastructure;
namespace Research.Application;
public static class Service
{
    public static void Load() => Repository.Read();
    public static void Caught() { try { Repository.Read(); } catch (StorageException) { } }
    public static void Unrelated() { try { Repository.Quiet(); } catch (StorageException) { } Repository.Read(); }
    public static void Incompatible() { try { Repository.Read(); } catch (ArgumentException) { } }
    public static void Rethrow() { try { Repository.Read(); } catch (StorageException) { throw; } }
    public static void Replace() { try { Repository.Read(); } catch (StorageException) { throw new InvalidOperationException(); } }
    public static void Filter(bool enabled) { try { Repository.Read(); } catch (StorageException) when (enabled) { } }
    public static void Interface(IRepository repository) => repository.Read();
    public static void Recursive() => Recursive();
    public static async Task AwaitCaught() { try { await Repository.ReadAsync(); } catch (StorageException) { } }
    public static void Unobserved() { _ = Repository.ReadAsync(); }
    public static void Branch(bool flag) { if (flag) Repository.Read(); else Repository.Quiet(); }
    public static void Repeat() { Repository.Quiet(); Repository.Quiet(); }
    public static void Nested() { try { try { Repository.Read(); } catch (StorageException) { throw; } } catch (Exception) { } }
}
