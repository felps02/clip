import { ChevronLeft, ChevronRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { formatNumber } from "@/lib/format";
import { cn } from "@/lib/utils";

export interface PaginationProps {
  page: number;
  pageSize: number;
  total: number;
  onPageChange: (page: number) => void;
  className?: string;
  itemLabel?: string;
}

export function Pagination({ page, pageSize, total, onPageChange, className, itemLabel = "itens" }: PaginationProps) {
  const totalPages = Math.max(1, Math.ceil(total / Math.max(1, pageSize)));
  if (total <= pageSize) return null;
  const from = (page - 1) * pageSize + 1;
  const to = Math.min(total, page * pageSize);
  return (
    <nav className={cn("flex flex-wrap items-center justify-between gap-3 text-sm text-fg-muted", className)} aria-label="Paginação">
      <p>
        {formatNumber(from)}–{formatNumber(to)} de {formatNumber(total)} {itemLabel}
      </p>
      <div className="flex items-center gap-2">
        <Button
          variant="secondary"
          size="sm"
          onClick={() => onPageChange(page - 1)}
          disabled={page <= 1}
          leftIcon={<ChevronLeft aria-hidden="true" />}
        >
          Anterior
        </Button>
        <span className="tabular-nums">
          {page} / {totalPages}
        </span>
        <Button
          variant="secondary"
          size="sm"
          onClick={() => onPageChange(page + 1)}
          disabled={page >= totalPages}
          rightIcon={<ChevronRight aria-hidden="true" />}
        >
          Próxima
        </Button>
      </div>
    </nav>
  );
}
