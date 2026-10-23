import { Alert } from "@/components/ui/alert";

export function FormError({ message, className }: { message: string | null | undefined; className?: string }) {
  if (!message) return null;
  return (
    <Alert variant="danger" className={className}>
      {message}
    </Alert>
  );
}
